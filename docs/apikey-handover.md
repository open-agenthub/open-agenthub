# Handing API-key data to a team-login user

## Decision

An admin hands the data of an owner from API-key or development mode to one chosen OIDC user, in
the admin area (*Users & seats → Hand over data from API-key mode*) or over
`POST /api/admin/handover/preview` and `/apply`. Nothing moves by name equality, and nothing moves
on its own when the install switches to OIDC (#501, follow-up of #492 and `docs/admin-bootstrap.md`).

- **Who.** Admins only. The source must be a single-user owner (`SingleUserOwnerStore`: the
  recorded key owner, `Auth:FormerApiKeyOwner`, `admin`, `dev`). The target must be a user who has
  signed in (a row in `app_users`), matched by exact name, and must not be a single-user owner
  itself. Only under OIDC: in API-key mode there is nobody to hand anything to.
- **Preview, then apply.** The preview counts per kind, lists renames, credentials, the size of
  the stored session data, what stays and which sessions block. It returns a token, protected with
  Data Protection, bound to the admin who previewed, valid 15 minutes, that fixes source, target,
  the credentials choice and the counts. Apply recounts inside the transaction and refuses a
  changed count (409 `preview_stale`), so what is applied is what the admin saw.
- **Running sessions block** (409 `sessions_running`, and the preview names them). A live pod
  keeps its presigned URLs and archives into the old owner's prefix, which the cleanup removes.
  Scheduled sessions cannot be paused; their CronJobs are suspended for the duration instead.

## What moves and what stays

Moves, in one Postgres transaction together with the audit row (`owner_handovers`): sessions,
projects, skills, MCP servers, webhook triggers, session files, usage history (`usage_monthly`
is merged by month, because the monthly budget gate reads it), messages between sessions, open
secret and permission requests, shares (a share that would point the target at its own data is
dropped; one somebody gave to both names stays once). Rows keyed by session follow the session.
Object keys stored in rows (`session_files.storage_locator`, `session_executions` keys) are
rewritten to the target's prefixes.

Name clashes do not stop the handover: the moved project gets ` (API key)`, a skill or MCP server
`-api-key` (skill names are kebab-case), numbered if that is taken too. The preview lists each.

Stays with the old name: personal API tokens (a bearer secret somebody may still hold; moved, it
would act as the target), the `app_users` row and its seat, roles, a usage limit set for the old
name, the cross-owner message audit. Chat threads of moved sessions end: they belong to the old
name's linked chat account, and a reply from there would act on the target's session. The
target's next question opens a thread in the target's own chat.

### Stored credentials: a separate opt-in

Provider logins and API keys, Git PATs, connected Git providers, stored secrets, the SSH key and
Git name/e-mail only move when the admin ticks *Also hand over stored credentials*. Unticked is
the default: these are someone's subscription and Git identity, and the admin handing over
sessions does not know that the key holder and the target are the same person. Without the tick
they stay with the old name, and a moved session that needs them starts without them: the CLI
shows its own sign-in, a PAT that is gone is skipped (`GitPatSelection.Apply`), a secret that is
gone is skipped and can be asked for with a secret request.

With the tick they merge into the target's secrets, server-side, secret to secret; no value
reaches the preview, the browser or the log. The target's own always wins: a single slot it has
filled keeps its value, an id it has is left alone, its defaults stay its defaults. Ids are kept,
so moved sessions and projects still point at their credential. The preview says per kind how
many move and how many stay because the target has its own. Values encrypted in Postgres (MCP
and webhook secrets) use fixed Data Protection purposes, not the owner, and move as rows.

## Order, failure and rerun

A Postgres transaction cannot include Kubernetes secrets or object storage, so the order is what
makes a failure safe:

1. Suspend the source's scheduled sessions (the CronJob remembers its own `suspend` in an
   annotation).
2. Copy: credentials into the target's secrets (only with the tick), then every object under
   `sessions/<owner hash>/<id>/` and `sessions/<owner name>/<id>/` to the target's prefixes. Page
   by page, an object whose copy already has the same size is skipped, progress goes to the log
   every 10 s; objects over 5 GiB are copied in parts.
3. Check again that nothing runs, then the transaction: recount, renames, owner columns, audit
   row `committed`.
4. Rebuild each scheduled session's CronJob for the target (its template mounts the old owner's
   secrets by name), delete the source's prefixes and (with the tick) its credential secrets,
   mark the audit row `completed`.

A failure before 3 restores the schedules and changes no ownership; the copies are additive and
a rerun skips what is there. A failure in 3 rolls back everything, the audit row included. A
failure in 4 leaves the data with the target and the row `committed`; every preview and apply
first finishes such rows. A second apply after a completed one finds nothing to move.

## Alternatives considered

- **Move by name equality** (the OIDC user called `admin` gets it). That is what happens without
  this feature, and it is the defect: on a provider with self-registration whoever registers the
  name first gets the data (`docs/admin-bootstrap.md`).
- **Rewrite owner columns when the chart switches to OIDC.** Rejected before (`docs/no-oidc-api-key.md`):
  a configuration change cannot know which OIDC user the key holder becomes.
- **Move credentials always, with the sessions.** Simplest for the common case where the key
  holder and the target are one person, but an admin handing over a team's pilot data would hand
  one person's model subscription and Git push rights to another without being asked.
- **Never move credentials.** Safe, but the key holder would have to sign in to every provider
  and re-enter every PAT and secret after the switch, which is exactly the friction this removes.
- **Refuse on name clashes.** Correct but unhelpful: the admin would have to rename things in
  somebody else's data before being allowed to hand it over.
- **Pause running sessions automatically.** A pause interrupts work in progress for a person who
  may not know the handover is happening; the preview names the sessions and the admin decides.

## Tests and proof

`OwnerHandoverPostgresTests` (real Postgres, the real stores' tables): every table, renames,
usage merge, rewritten keys, shares; a failure injected part-way through the switch and at the
audit row leaves every table unchanged (without the transaction both fail); stale counts; rerun;
the credentials opt-in both ways; running sessions; a failed copy; a failed cleanup finished by the
next call; the admin-bound token; source and target checks. `OwnerHandoverCredentialsTests`: the
merge rules per kind. `handover-card.test.js`: the card. `tests/ApiKeyHandoverProof/` runs the real
backend through API-key mode and OIDC with a local issuer, a real kube-apiserver, object storage
and Chromium.
