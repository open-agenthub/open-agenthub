# Historical Claude state cleanup proof

Use disposable services only. Start rootless object storage and Postgres as in
`tests/SessionStorageProof` and a rootless cluster API using the skill runbook.
Configure `S3__ServiceUrl`, `S3__Region`, `S3__Bucket`, `S3__AccessKey`,
`S3__SecretKey`, `ConnectionStrings__Postgres`, and `PROOF_KUBECONFIG`.
The proof creates a namespace named `claude-cleanup-proof` and synthetic storage
keys and removes those storage keys when finished. Use an empty disposable database.

```sh
node tests/ClaudeStateCleanupProof/produce.cjs /tmp/claude-cleanup-proof
dotnet run --project tests/ClaudeStateCleanupProof -- /tmp/claude-cleanup-proof
node tests/ClaudeStateCleanupProof/verify.cjs /tmp/claude-cleanup-proof
```

`produce.cjs` uses the real server, real installed node-pty and a harmless sleeping
provider command. `NODE_PATH` must point to installed runtime dependencies (the
session image has `/opt/session-agent/node_modules`). It deliberately supplies the
pre-fix driver exclusions to reproduce historical archives even after the producer
fix lands. The producer starts with an environment allowlist, synthetic loopback callback
credentials, and a private nonexistent watcher pidfile; it never inherits live
session transport variables. All fixture credentials are synthetic. The admin HTTP proof substitutes
only authentication, using a local header to exercise anonymous/nonadmin/admin
identities; authorization, controller, registry, lock, object storage and cluster
client/API are real. No kubelet, external upload-URL holder, object version history,
or production credentials are exercised. No production cleanup is run.
