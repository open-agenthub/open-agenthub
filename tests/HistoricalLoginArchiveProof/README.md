# Historical login archive audit and cleanup proof

Use disposable services and synthetic credentials only. Run from the repository:

```sh
node tests/HistoricalLoginArchiveProof/tag-audit.cjs
node tests/HistoricalLoginArchiveProof/produce.cjs /tmp/historical-logins open-agenthub-0.4.0
node tests/HistoricalLoginArchiveProof/produce.cjs /tmp/historical-first open-agenthub-0.1.0
for agent in codex cursor openclaw opencode; do
  node tests/HistoricalLoginArchiveProof/produce-providers.cjs /tmp/historical-logins/$agent $agent
done
dotnet run --project tests/ClaudeStateCleanupProof -- /tmp/historical-logins --provider-logins
```

The tag audit emits JSONL for every available release tag, checks the common tar
exclusion mechanism, and reports each driver's default auth filename/additional
excludes. Unknown archive mechanisms fail rather than guessing. It needs Git tag
history, not only a shallow checkout.

The historical producer executes the actual tagged 0.1.0/0.4.0 server with real
node-pty, tar/gzip and HTTP upload. Only fixed scratch paths are relocated to avoid
colliding with a live session's /tmp files. A sleeping fake provider command keeps
it offline; access/refresh credentials are synthetic. Its environment is an
allowlist and has no live callback or upload destination.

Other-provider fixtures run the actual current common server with exclusions
deliberately disabled to test defensive cleanup of foreign/legacy uploads. They
are NOT evidence that released non-Claude runtimes archived those stores.

The executable reuses #316's real S3/Postgres/API/admin-controller proof and adds
both endpoint authorization cases, all-provider preview, lock/live-producer
skips, five archive rewrites, retained raw tar-entry SHA256, object byte-idempotence,
and malformed-object isolation. Use the service environment setup from
`tests/ClaudeStateCleanupProof/README.md`; no kubelet, real provider credentials or
production revocation is involved. Run with `ulimit -c 0` during development.
