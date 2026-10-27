# Historical provider login files in state archives

The release-tag audit found an archive-selection gap for Claude's primary login
file. This is evidence about archive behavior, not a claim that every session had
a login file, that any production archive was accessed, or that old tokens remain
valid. No production archives or real credentials were inspected.

## Version boundaries

The reproducible audit checks all 49 release tags in both naming families through
1.2.0, including release candidates and `v0.6.0` (which has no matching long-name
tag). It checks the archive script and each available driver's login filename and
additional exclusions, not only the presence of a driver file.

| Runtime | Known login stores | Release archive selection |
| --- | --- | --- |
| Claude | `.claude/.credentials.json` | Not excluded in 0.1.0, 0.2.0, 0.2.1, 0.3.0, 0.3.1, 0.4.0. Excluded from 0.5.0 through 1.2.0, including RCs. |
| Codex | `.codex/auth.json` | Excluded from its first tagged runtime, 0.5.0, through 1.2.0. |
| Cursor | Canonical `.cursor/auth.json`; Linux CLI path links to it | Excluded from its first tagged runtime, 0.5.0, through 1.2.0. |
| OpenClaw | Root `auth-profiles.json`; nested `agents/*/agent/auth-profiles.json` and `openclaw-agent.sqlite` plus WAL/SHM | All excluded from its first tagged runtime, 0.5.0, through 1.2.0. |
| OpenCode | `.opencode/auth.json` and `.opencode/mcp-auth.json` | Both excluded from its first tagged runtime, 0.13.0, through 1.2.0. |

The 0.1.0 and 0.4.0 session servers were run with real PTYs, tar/gzip and a
loopback HTTP upload, with synthetic access/refresh tokens in the credential file.
Both uploaded that file. Starting with 0.5.0 the common archive excludes the
provider's `stateDir/authFilename`. This audit covers known default paths; custom
images, relocated directories and additional CLI-generated credential copies need
separate review. Other runtimes are covered defensively by cleanup, without
claiming a released login-file exclusion gap for them.

Configuration backups are a separate issue: `.claude/backups` can hold MCP secrets
and account metadata even when the primary login file is excluded. See
[the configuration-backup cleanup decision](claude-state-backup-cleanup.md).

## Admin cleanup

`POST /api/admin/session-storage-cleanup/provider-logins` accepts the same owner
and apply fields as the configuration-backup endpoint. The owner is the exact
session-record username, not necessarily an email; administrators can read
`users[].owner` from `GET /api/admin/roles` or the user-role rows in the admin UI.

```sh
curl -fsS -X POST https://example.com/api/admin/session-storage-cleanup/provider-logins \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  --data '{"owner":"owner@example.com","apply":false}'
```

Repeat with `apply:true`. The new endpoint checks all five provider archive keys
under the hashed owner prefix, including orphaned or older provider archives in a
session prefix. It removes the stores listed above and Claude configuration
backups. The existing `claude-backups` endpoint retains its backup-only behavior.
Counts are per candidate provider archive: `scanned`, `missing`, `clean`,
`rewritten`, `skipped`, `failed`; preview `rewritten` means would change.

The same maintenance lock and actual pod/CronJob/Job guards apply. No owner UI/API
pauses scheduled sessions while retaining the record: immediate scheduled cleanup
requires cluster-operator maintenance. Interactive Pause submits deletion but does
not wait for pod exit; retry cleanup until producers are actually absent. Do not
use session Delete to prepare this cleanup, because it removes the record and state.
See the backup-cleanup decision for producer handling, archive size limits,
external presigned writers and noncurrent object versions/backups. Retained tar
headers, payloads and padding stay byte-identical; rewriting entries was rejected
because it changes metadata. Missing or clean objects are never uploaded.

## Which owners should revoke a login

Owners who used a Claude subscription/OAuth login in sessions running releases
0.1.0 through 0.4.0 should revoke the corresponding Claude Code authorization at
the provider and sign in again, because a stored archive could contain its access
and refresh tokens. Include every connected account used by those sessions; the
current default account need not be the account used then. File removal is not
remote token revocation, so cleanup and revocation are separate actions.

The provider's [official logout guidance](https://support.claude.com/en/articles/10310342-how-do-i-log-out-of-all-active-sessions)
says to use web Settings → Account to terminate the relevant Claude Code sign-in;
if not listed there, remove its authorization token in Settings → Claude Code.
When the affected sign-in cannot be identified, use the account's logout-all
control, then authenticate again. That control covers Claude Code sign-ins but
does not revoke API keys. Reconnect the account in AgentHub after revocation;
clearing only a local CLI credential file or removing a hub account is not evidence
that a previously copied token was invalidated at the provider.

An API-key-only session does not by itself establish an OAuth login-file exposure.
Rotate an API key if review establishes that it was actually present in archived
credentials/configuration; rotate literal MCP credentials according to the
separate backup issue. There is no source-established primary-login exclusion gap
for the other runtimes in the checked releases, so this finding alone does not
justify blanket revocation of their accounts. If a custom/foreign archive actually
contains one of their login stores, revoke each represented login at its original
provider (which may differ from the runtime name), and rotate any API keys it held.

No real provider revocation was performed or tested by this proof. Provider-side
controls are documented above from its public guidance; this does not certify the
validity or revocation state of any historical token.
