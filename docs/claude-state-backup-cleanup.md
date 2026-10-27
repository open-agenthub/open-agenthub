# Removing historical Claude configuration backups

State archives written by versions 1.0.0 through 1.2.0 can contain
`.claude/backups/`: the CLI copies `~/.claude.json` there. That file contains
account metadata and user-scoped MCP configuration. Excluding only the primary
credential file did not exclude these derived copies. New archives exclude this
directory, and restore excludes it even when reading an older archive.

## One-time administrator cleanup

Deploy the archive exclusion and restore fix first. Update existing session
runtimes too: an old running pod can upload another affected archive. For each
owner, an authenticated administrator calls:

```sh
curl -fsS -X POST https://example.com/api/admin/session-storage-cleanup/claude-backups \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  --data '{"owner":"owner@example.com","apply":false}'
```

Repeat with `apply:true` to rewrite. The owner is the actual registry username;
the backend derives the hashed owner prefix, never a raw username storage key.
Counts are `scanned`, `missing`, `clean`, `rewritten`, `skipped`, and `failed`.
In preview, `rewritten` counts archives that would change. Repeating apply leaves
already clean objects byte-identical. Run once per affected owner; rerun until
no failures or eligible affected archives remain. Counts deliberately omit archive
names and exception text because either can contain sensitive data.

The cleanup holds the same database advisory lock as session resume/deletion.
It skips a locked session or any session with a named/labeled pod (including
terminating pods), a CronJob, or an owned Job. Pause interactive sessions and wait
for actual pod exit before retrying. Scheduled sessions should use the updated
runtime: their next state upload is clean; their retained CronJob causes cleanup
to skip them. Do not operate external archive writers during cleanup. The lock
coordinates hub operations, not someone holding an old presigned upload URL.

A registry status check was rejected because pause can return before the final
SIGTERM upload. Conditional object writes were also rejected: real storage-server
2.1.0 accepted a stale `If-Match`, silently overwriting a newer object. Checking
actual producer absence under the maintenance lock avoids that lost-history race.
Unknown cluster status fails closed; cluster failures count as failed archives.

The scrubber decompresses in memory with a 256 MiB uncompressed limit. Malformed
or oversized archives remain untouched and count as failures. It copies original
tar blocks for every retained entry, including headers, extended metadata, file
contents and padding. Re-serializing entries was rejected because it changes
metadata. Only backup entries disappear; gzip bytes necessarily change. Clean
archives are never uploaded again. Other providers, files and owners are untouched.
This rewrites current objects only: storage administrators must separately remove
noncurrent versions, snapshots, replicas and independent bucket backups according
to their retention policy.

## Credentials and metadata to review

`claude/mcp-config.mjs` merges the entire effective `mcpServers` configuration into
`~/.claude.json`. Raw library MCPs and inline MCPs retain literal HTTP `headers`
(including bearer/API tokens) and stdio `env` values. Owners should revoke/rotate
all external credentials present in the MCP configurations used by affected
sessions, including values since removed from current configuration: backups can
hold earlier values. Replace library/inline configuration with the rotated values.
Deleting an archive or a session does not revoke those external credentials.

Built-in browser, sessions, network and files entries contain command/args only.
Their stdio proxies inherit the session callback token from the process environment;
they do not write it as `env` into this config. The skill-library HTTP entry has
`X-Agent-Token` containing the callback token. The default local skills proxy
replaces that HTTP entry with command/args; disabling that proxy or using an older
runtime can leave the token in config/backups.

API library MCPs and ephemeral API MCPs contain `X-AgentHub-Mcp-Token` gateway
tokens (default lifetime twelve hours). Their downstream secrets stay on the hub;
the gateway header is still a credential and can appear in backups. Custom image
user-scoped MCP entries can also be retained by the merge and need owner review.

Deleting the session removes its registry row. Callback authentication uses
`GetByCallbackTokenAsync`; gateway requests additionally require the session row
for the token's owner/session id. Both consequently reject tokens from a deleted
session, even if a gateway token is still cryptographically valid. Pausing is not
revocation. For a suspected exposed hub token, delete the affected session after
preserving needed work and create a new session. Existing presigned object URLs
are different: deleting a registry row does not invalidate their storage signatures;
they remain valid until expiry (normally twelve hours). Apply storage-side controls
if immediate revocation is required.

The account metadata in `.claude.json` is personal data. Its presence alone does
not establish exposure of provider login credentials, whose primary file is
excluded separately. Review any additional custom configuration/backup values;
rotate provider credentials if those values contained them. No real credential is
needed to run the proof below.

## Reproducible proof

`tests/ClaudeStateCleanupProof` uses the real `common/server.js`, PTY, tar/gzip and
HTTP uploader to generate a legacy archive with synthetic secrets. It drives the
real admin controller and scrubber against real object storage, Postgres registry
and advisory locks, and a rootless cluster API. It checks authorization, preview,
owner isolation, idempotence, malformed objects and producer guards. Per-entry
SHA-256 covers the original tar header, payload and padding, including a GNU long
name and symlink. An independent GNU tar listing and extracted file hash check
verify restoration. See the proof README for invocation and limits.
