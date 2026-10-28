# Credential staging and stored session state

The shared credential installer atomically replaces a login file by writing a sibling
`.<basename>.<pid>.tmp` with mode 0600, then renaming it. Released archives excluded the
final filename but could read the sibling during the write-to-rename window. The same
window exists for refresh and credential replacement. This is a possible capture, not
evidence that any production archive contains credentials.

## Version boundary and scope

The shared writer first appears in commit `0a9e86e`, released in 0.13.0. Its naming
formula and callers are present through 1.3.0 inclusive for Claude, Codex, Cursor,
OpenClaw and OpenCode. All 51 local release tags were audited, including both tag
families and release candidates; 22 tags contain this writer. Earlier releases lack
this shared writer. This finding does not bound temporary files made internally by
an agent CLI. The separate canonical-login-file history is in
[provider-state-login-cleanup.md](provider-state-login-cleanup.md).

## Decision

Archive and signature exclusions now derive a sibling temporary-file pattern from
every excluded path, including nested wildcard credential stores. Keeping the existing
adjacent rename preserves atomic installation on the target filesystem. Moving credential
staging to a generic temporary directory was rejected because a different filesystem
would make rename fail; exact final-name exclusion leaves the capture window open.

The existing owner-scoped provider-login cleanup also removes the shared writer's
positive numeric-PID sibling names, only where the corresponding canonical credential
store is known. It retains similarly named history, notes and other providers' paths.
A broad `*.tmp` cleanup was rejected because it would delete unrelated state. The same
admin preview/apply calls, hashed owner keys, producer guards, counts and limitations
from the provider cleanup apply. Operators who already ran that cleanup must run it
again to cover staging files. No schema migration or automatic scan runs at startup.

Archive scratch files now live in separate `mkdtemp` directories (mode 0700) under
`TMPDIR`, removed after upload, archive failure or privacy cancellation. The fixed
`/tmp/state.tgz` path allowed processes sharing a runtime to collide. Making that one
filename less predictable without a private directory would still leave intermediate
files exposed. Abrupt process termination can leave a private directory until the
runtime's temporary storage is removed.

## Owner action

Owners whose provider credentials were installed or refreshed on 0.13.0–1.3.0 should
consider those credentials potentially present in stored state, revoke provider login
sessions and reauthorize. Rotate API keys represented in those login stores; multi-provider
stores can require action at more than one provider. Cleaning objects does not revoke
credentials. Provider account controls, rather than deleting only a hub session, determine
provider-token revocation. The verified Claude account controls and the separate MCP
secret guidance are linked in the provider and backup cleanup documents. We did not
inspect production objects, prove an actual capture, test real credential revocation,
or audit every CLI-internal cache. Object-store versions and external copies require
separate operator handling.

## Proof

`tests/CredentialTempArchiveProof/tag-audit.cjs` checks the released writer and callers.
`run.cjs` starts the real common server, uses its HTTP credential-refresh route and real
writer, holds rename until GNU tar finishes, and captures the real periodic archive
at a loopback receiver. All five providers capture the synthetic secret with 1.3.0's
writer/archive modules; all five omit it with the fix, preserving history. OpenClaw
uses the nested per-agent credential store. This deterministic ordering proves the
window without a probabilistic timing loop or real credentials.

The real admin controller, registry locks, Postgres, object storage and Kubernetes API
then clean those captured archives using `tests/ClaudeStateCleanupProof` with
`--credential-temps <race-output-directory>` (the first argument remains the standard
backup fixture directory). The same run checks preview, authorization, producer/lock
guards, malformed archives, owner isolation and byte-idempotence. Retained raw tar-entry
SHA256 hashes match; `verify.cjs` independently compares GNU tar listings, extracted file
hashes and modes, and absence of synthetic secret bytes. Runtime regressions hold the
real writer's root/nested staging files during tar and signature collection and check
real private-directory permissions and cleanup.

Proof processes use minimal environments, synthetic loopback transport and private
watcher paths. Older proof fixtures now redirect the historical fixed archive scratch
filename too. Earlier reports for #317/#320 have been qualified: their own PUTs were
loopback-only, but their shared scratch filename could have collided with another
archiver. No collision was observed; another process reading those bytes cannot be
categorically excluded. These proofs do not establish production-object contents.
