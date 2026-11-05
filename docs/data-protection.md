# Shared Data Protection ring (#458)

## Decision

All backend replicas use `PostgresKeyRepository` and the fixed application name
`open-agenthub`. The existing migration advisory lock creates the table before the
framework can read it. The repository keeps every XML element, including revocations;
keys are never pruned merely because their encryption lifetime ended. The framework
still rotates Data Protection keys on its normal schedule.

Keys are wrapped with the framework's certificate XML encryptor, using an RSA PEM
certificate/private key from a separate chart Secret. Startup validates every stored
key before accepting requests: a missing or wrong wrapping identity must fail rather
than silently create a replacement ring. No new keys are written to HOME.

The chart generates `agenthub-data-protection` once, reuses it with `lookup`, and marks
it `helm.sh/resource-policy: keep`. `dataProtection.existingSecret` selects an
operator-managed Secret with `tls.crt` and `tls.key`. Offline rendering cannot perform
lookup: declarative renderers must use an existing Secret or preserve the generated
manifest. Re-rendering a new identity over the old Secret strands the database ring.
Secret changes require a backend rollout because certificate input is read at startup.
The certificate is a wrapping identity, not a network TLS endpoint or a trust assertion.

The database backup and wrapping Secret are a recovery pair. Database-only readers
cannot unwrap the ring; anyone holding both can decrypt protected values. Restrict
Secret access and protect cluster backups accordingly. The generated Secret survives
uninstall intentionally; operators remove it only after retiring the matching database.

## Upgrade and recovery

Startup checks MCP catalog, webhook, and session API secrets. If the old file ring
survives in HOME, it attempts to decrypt with the old content-root application name,
then rewrites only successfully recovered payloads using the shared ring. For an
exported old ring, set `DataProtection:LegacyKeysDirectory` and the exact old
`DataProtection:LegacyApplicationName` (including its trailing directory separator).
Mount that export read-only. Automatic key generation on the legacy provider is disabled.
The old directory can be removed once all readable values have migrated. Migration
compares the original payload/row version so a concurrent user repair is not overwritten.

Values encrypted before the update whose keys are already lost require re-entry.
Unreadable payloads stay in the database. Catalog and webhook lists return
`needsReentry=true`; catalog entries retain `hasSecret=true`. UI hints explain how
to re-enter catalog secrets or recreate webhook triggers. Startup logs show aggregate
counts and recovery instructions, never payloads. Secret-dependent gateway requests
return 503; configuration assembly refuses an unreadable selected source. A null secret
update retains the old payload; only explicit replacement/clearing changes it.

Old transient gateway tokens and cleanup previews must be reissued (restart the session
or repeat the dry run). They are not durable records and are not migrated. Session API
sources with lost secrets must be registered again.

Wrapping-certificate rotation needs overlap: load the old certificate/key pairs under
`DataProtection:PreviousCertificates:<index>:CertificatePem` and `PrivateKeyPem` on every
replica before switching the active pair. The backend supports these additional decryption
certificates; inject them through operator configuration. Keep old private keys while
any database ring entries or backups need them. Rotation does not rewrap historical rows.

## Rejected alternatives

- Pod-local files or memory: neither survives a pod replacement or shares keys across replicas.
- A shared filesystem: adds a volume and availability dependency to an already database-backed app.
- A plaintext database ring: database backups would then contain both ciphertext and its decryption keys.
- Deriving keys from the database password or login API key: couples unrelated rotations and grants
  a database reader or login-key holder unintended access to historical protected values.
- A custom symmetric XML encryption format: adds cryptographic implementation and versioning work
  when the framework already supports certificate wrapping and overlapping decryption identities.
- Silently dropping unreadable values: hides required operator action and turns configured
  authentication into missing authentication. A separate recovery state preserves the evidence.

## Proof

`tests/DataProtectionProof` runs real backend processes on separate loopback ports against
one Postgres schema, exercises HTTP reads across replicas, a restart and an empty-HOME
replacement, inspects encrypted ring storage, tests wrong-certificate startup failure,
and checks migration and explicit repair. The same scenario runs in the Postgres test suite.
