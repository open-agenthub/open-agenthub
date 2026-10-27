# Credential staging archive proof

Run from a worktree with runtime dependencies installed and GNU tar available:

```sh
node tests/CredentialTempArchiveProof/tag-audit.cjs
node tests/CredentialTempArchiveProof/run.cjs /tmp/credential-race-before --baseline --expect-leak
node tests/CredentialTempArchiveProof/run.cjs /tmp/credential-race-after
```

The baseline reads the released 1.3.0 writer and archive modules from Git. Both paths
use the working tree's real common server and private scratch allocation. Only the
periodic timer is accelerated; credential HTTP, writing, tar, gzip and upload are real.
The rename/tar barriers deliberately impose the vulnerable ordering, using synthetic
credentials and an isolated loopback receiver. No production endpoint is inherited.

Run the real-storage harness described in `tests/ClaudeStateCleanupProof`, appending
`--credential-temps /tmp/credential-race-before`, then independently verify its downloads:

```sh
node tests/CredentialTempArchiveProof/verify.cjs /tmp/credential-race-before
```

See `docs/credential-temporary-state-cleanup.md` for boundaries and rejected alternatives.
