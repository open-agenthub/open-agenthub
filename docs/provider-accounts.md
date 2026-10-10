# Several provider logins per user, one per session, swappable while the pod runs

A provider login — the file a CLI writes after `/login`, device auth or `agent login` — used to be
one per user and per provider: the `claude-u-<hash>` secret held exactly one `credentials.json`,
every new session got it, and a login performed inside any session overwrote it. Somebody with a
personal plan and a team plan, or two organisations, had to re-authenticate every time they changed
hats, and doing so silently replaced the login every other running session would be restored with.

This file records how that became *accounts*: what is stored where, how a login turns into a new
account rather than an overwrite, how a session picks one, and how a running pod is moved from one
account to another.

## Where accounts live: one secret per provider, many keys

The provider secret keeps its name (`claude-u-<hash>`, `codex-u-<hash>`, …) and gains two kinds of
keys:

```
accounts.json                  index: [{id, label, identity, createdAt, lastUsedAt, isDefault}]
3f9a1c2b7e0d4a61.credentials.json   the file of account 3f9a1c2b7e0d4a61
9b02ee1d5c7f8a30.credentials.json   the file of another account
```

The file name after the dot is the provider's own (`credentials.json`, `auth.json`,
`auth-profiles.json`), which is what makes the pod side unchanged — see below.

**The alternative was one secret per account** (`claude-u-<hash>-<accountId>`) with the index kept
in Postgres or derived from a label selector. It was rejected for three reasons:

- Every reader of a user's logins would have to list secrets by label, and `DeleteUserSecretsAsync`
  (the account purge) already relies on that label for *everything* the user owns. Splitting the
  index between a label query and a database row is how an account purge ends up leaving a login
  behind.
- A session's pod spec names the secret it mounts. With one secret per account, switching a
  session to another account means editing the pod spec, which Kubernetes does not allow on a
  running pod — the in-place swap below would have been impossible by construction.
- The secret has a 1 MiB limit and a provider file is capped at 64 KiB
  (`ProviderCredentialValidator.MaxBytes`), so a dozen accounts fit with room to spare. The limit
  that would have argued for separate secrets does not bind.

The index is written to the same secret as the files, in the same `Replace`, so the two cannot
drift apart by a failed second write. A reader still reconciles: an index entry whose file is
missing is dropped, and a file with no index entry gets one named after its id. Both are cheap and
both turn a corrupted secret into a degraded listing rather than an exception.

### Lazy migration of the single-file layout

A secret written before this change holds one bare `credentials.json`. The first time the backend
reads it — a listing, a session start, a writeback — that key is renamed to
`default.credentials.json` and an index with one entry (`id: default`, `label: Default`,
`isDefault: true`) is written next to it. Nothing has to be run by the operator, and a secret read
twice is migrated once: the second read finds the index and leaves the secret alone.

The legacy key is moved, not mirrored. Keeping a copy under the old name would have let a
downgraded backend keep working, but a mirror is a second source of truth: the first writeback that
updated one and not the other would leave two different tokens for "the" login, and Anthropic's
refresh-token rotation means the stale one is already dead. A downgrade after migration needs the
secret recreated by a login; that is the cost accepted.

## Identity is display only

Each account carries an `identity` — an e-mail, an organisation name, and a stable key used for
matching — so a person can tell two logins apart in a dropdown. Nothing is verified. The values
come from what the CLI wrote to disk after its own login, which means they are as trustworthy as
the file next to them and no more: a user who edits `~/.claude.json` inside a session changes the
label, not whose token is used. The backend therefore never authorises anything on an identity,
only on the owner and the session, and the frontend shows it as a hint.

Per provider (what was checked, and against which version):

- **Claude**: `credentials.json` holds `claudeAiOauth` with opaque tokens and no account data.
  Identity sits in `~/.claude.json` under `oauthAccount` — `accountUuid`, `emailAddress`,
  `organizationUuid`, `organizationName` — which the CLI writes at login. Confirmed in the strings
  of the 2.1.285 binary (`oauthAccount.emailAddress`, `oauthAccount.organizationName`,
  `oauthAccount.organizationUuid`); the runtime pins 2.1.287 and the file name has not moved
  between the two. The Claude watcher reads it and sends it as the `X-Agent-Identity` header of
  the writeback; the key is `accountUuid:organizationUuid`, so the same person in two
  organisations is two accounts, which is the case this feature exists for.
- **Codex**: `auth.json` has `tokens.id_token`, a JWT whose payload carries `email` and the
  `https://api.openai.com/auth` claim with `chatgpt_account_id`. The backend decodes the payload
  without checking the signature — there is no key to check it against and, per the paragraph
  above, nothing is decided on it. Key: `chatgpt_account_id` when present, else `email`.
- **Cursor**: `auth.json` has `accessToken`, a JWT whose payload has `sub`; an e-mail is read if
  one is present and nothing is assumed about it. Key: `sub`.
- **OpenClaw**: `auth-profiles.json` holds `profiles` keyed `provider:name`; a profile may carry an
  `email`. The identity is the sorted profile key list plus the first e-mail found. Key: that list.

When nothing can be extracted the account still exists, with a label the user can edit
(`PATCH /api/credentials/accounts/{agent}/{id}`) and no identity line.

## How a login inside a session becomes a new account

Every session pod uploads its provider file through `PUT /internal/sessions/{id}/{agent}-credentials`
whenever it changes (the watchers). That endpoint used to replace the user's one file. Now it
decides which account the upload belongs to, in this order:

1. **The session has an account mounted** (`credential_id` on its record) and the upload carries no
   identity, or the same identity key, or the account had no identity yet: the upload *is* that
   account rotating its token. Update it in place. This is the common case and the one the
   refresh-token rotation makes mandatory — a token Anthropic hands out replaces the one it was
   redeemed with, so the account must track it or it dies.
2. **The session has an account mounted but the identity key differs**: the user ran `/login`
   inside the session and signed in as someone else. Overwriting the mounted account would
   silently rename it; instead a new account is created and the session's `credential_id` is moved
   to it. The old account keeps its token.
3. **No account mounted, identity known and matching an existing account**: that account is
   updated and the session is attached to it. Nothing is mounted when the user had no login at all
   or when the account they wanted did not exist yet, so matching by identity is what stops a
   second login to the same organisation from creating a duplicate.
4. **No account mounted, no identity, but the bytes equal an existing account's file**: the
   restored copy was uploaded back. Attach, do not duplicate. (The watcher baseline prevents most
   of these uploads; this is the belt to that brace.)
5. **Otherwise**: a new account. It becomes the default if it is the first one.

The endpoint returns the account id so the controller can persist it on the session record. The
stable key, not the e-mail, is what matching uses — an e-mail that moved to another organisation is
a different account.

## Which account a session gets

`CreateSessionRequest.CredentialId` names an account; it is checked against the index at creation
so a typo fails the request rather than the pod. Omitted means "the default account, resolved at
each start" — stored as null, so changing the default later changes what a resumed session gets.
Duplicate copies it; `UpdateSessionRequest.CredentialId` changes it for the next start (an empty
string resets to the default, matching the convention of the other optional update fields).

The pod spec mounts the account through an `items` projection:

```yaml
secret:
  secretName: claude-u-<hash>
  optional: true
  items:
    - key: 3f9a1c2b7e0d4a61.credentials.json
      path: credentials.json
```

so `/secrets/claude/credentials.json` is still where the Claude entrypoint looks, and none of the
four entrypoints changed. `optional: true` was already set, and it carries a second meaning here:
a projected key that does not exist is skipped rather than failing pod setup. A user with no login
yet gets an empty mount, the entrypoint sees no file, and the in-session login path runs exactly
as before.

The preflight for unattended sessions (`MissingCredentialDiagnostic`) now asks whether the
*resolved* account's key exists, not whether the secret has any file at all — an autonomous session
pinned to an account that was removed fails at creation with that message rather than starting a
pod that cannot authenticate.

## Switching the account of a running session

`PATCH /api/sessions/{id}/credential {credentialId}` is accepted for the owner, for a `Running`
session in `Subscription` mode (API-key sessions have no file to swap, and a scheduled session has
no live pod). The backend writes the new `credential_id`, then pushes the account's file to the
session agent: `PUT http://<podIp>:7681/agenthub/credentials` with the session's callback token
and an `X-Agent-Provider` header. The agent:

1. Refuses anything but its own provider (`409`), and anything that is not a JSON object the
   provider's own validator accepts (`400`). There is no path in the request; the file is written to
   the one location the driver names (`$HOME/<stateDir>/<authFilename>`, which is where every
   entrypoint already puts the restored copy). This is deliberate — an endpoint that took a path
   would be a write primitive into the pod's home directory, authenticated by a token that every
   process in the pod can read from the environment.
2. Records the file's hash as a **watcher baseline** before writing it (see below).
3. Writes the file atomically (temp file and rename, mode 0600) and runs the provider's
   post-install step — OpenClaw reads its credentials from SQLite, so the JSON is imported there
   too, the same step `login.sh` and the entrypoint perform.
4. Stops the agent process and starts it again with the provider's resume command, in the same
   pod and working directory, so the conversation continues. The terminal clients see one
   `[agent]` line and the chat UI one `agenthub` info event; nothing else about their connection
   changes.

### The watcher-baseline problem

Each runtime runs a credential watcher next to the agent, polling the provider file and uploading
it when its hash changes. The entrypoint hands the watcher the hash of the file it just restored
from the secret, so the restore is not uploaded straight back. A file swapped in by the agent is a
*change* by that definition: the next poll would upload the just-installed account's bytes to the
backend — harmless in content, but it would land on whatever `credential_id` the session has, and
in the window between the backend writing the new id and the pod finishing the swap, that is a
race between two correct answers.

The watcher process is started by the entrypoint and the session agent has no handle on it, so the
baseline cannot be a function call. It is a file: `$HOME/.agenthub/credential-baseline`, holding
the hash the agent is about to install. The watcher reads it on every poll and adds its content to
the set of hashes it treats as already uploaded. A set rather than a single value, because the
poll may land between the baseline write and the credential write: with a single value the
baseline would be the new hash and the still-old file would look like a change. With the set, the
old hash is the one the watcher last uploaded and the new one is the baseline, and neither triggers
an upload.

Keeping the watcher a separate process and signalling it through a file was chosen over moving it
into the session agent. Folding it in would have been cleaner, but the watchers are per runtime
and pinned to the quirks of each CLI's file (OpenClaw's SQLite export, Cursor's symlinked path),
while the session agent is shared and deliberately knows nothing provider-specific.

### Restart, or reload in place?

Whether the CLIs would notice a swapped file without a restart was checked as far as possible
without a cluster:

- **Claude Code 2.1.285** (the locally installed binary; the runtime pins 2.1.287) contains a
  change check keyed on the mtime of `.credentials.json` (`lastCredentialsMtimeMs`,
  `credentialsChangedOnDisk`) that clears its token cache when the file changes. So the token
  *would* likely be picked up live. What would not be picked up is `oauthAccount` in
  `~/.claude.json`, which the CLI only rewrites at login and consults for the organisation — the
  binary also compares `accountUuid`/`organizationUuid` across reads to detect a profile change.
  A live swap would run one account's token under another account's organisation metadata, and
  what that does to requests is exactly the kind of thing that cannot be verified from strings.
- **Codex, Cursor, OpenClaw**: not verified. The Codex CLI is Rust and reads `auth.json` through
  its own auth manager; nothing was found either way. OpenClaw reads SQLite, not the file.

Given that, every provider is restarted with resume. The cost is a few seconds and a resume that,
for a session started in this pod, finds its conversation on local disk (Claude by the session id
it was started with, Codex by `resume --last`, Cursor by chat id, OpenClaw by its session file).
The alternative — reloading in place where a CLI supports it — saves those seconds for Claude only
and trades them for a state that could not be verified. If a later version exposes a documented
reload, the swap route already has the driver hook (`installCredential`) to make the restart
conditional per provider.

The restart reuses the server's existing "retry fresh" path for a resume the CLI does not
recognise: a swap whose resume fails for a provider that cannot find its session starts the agent
fresh exactly once, as a cross-pod resume already does, rather than leaving the pod with no agent.

## What is deliberately not done

- **No account selection for API-key sessions.** Those read a key from the general credentials
  secret; a second key is a second field, not an account, and belongs to that dialog.
- **No verification of identity against the provider.** The e-mail is read from a file the user
  controls. Any check would need a provider API call with the user's token on every listing, and
  the value of the feature is telling two logins apart, which the file does.
- **No automatic switch on a failing token.** A session whose account expired fails as before.
  Choosing another account on the user's behalf would hide which login is broken.
- ~~The remote API and the MCP tools accept `credentialId` but do not list accounts.~~ Since
  `docs/credential-scopes.md`: `GET /api/remote/credentials` and the `credentials_list` tool list
  the accounts (keyed like `GET /api/credentials/accounts`), the git PATs and which API keys are
  stored — narrowed to what the calling token is allowed to use — and `session_create` takes
  `credentialId` on every MCP surface. The dropdown in the session dialogs also shows from the
  first account now, rather than from the second, so a person sees which login a session runs on.
