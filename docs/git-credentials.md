# How a session gets credentials for a git repository

A session clones through one of two mechanisms: a **connected provider**, where the user completed
an OAuth flow and AgentHub holds a refreshable token, or a **manually stored personal access
token**, for an instance with no OAuth app configured. Both now arrive the same way, and this file
records why that changed.

## One host-bound credential store, no second helper

The pod gets a single `~/.git-credentials` file, built per session into the `gitcreds-<id>` secret.
Every line binds one credential to one host:

```
https://x-access-token:<token>@github.example.com
https://oauth2:<token>@gitlab.example.com
```

A manually stored PAT used to work differently. It was projected into the pod as a file and
installed as a credential helper with no host attached:

```sh
git config --global --add credential.helper '!f() { echo "username=oauth2"; echo "password=$(cat /secrets/creds/gitlab_token)"; }; f'
```

Git only consults a helper after a 401, which made this look narrow. It was not. Measured against
git 2.47.3, with that helper configured:

```
$ printf 'protocol=https\nhost=evil.example\n\n' | git credential fill
username=oauth2 password=glpat-SECRET
```

Any host that answered 401 was handed the user's token — including a host chosen by a prompt
injected through a cloned README. With the store entry instead, the same query returns nothing,
while the host the user named still works. That is the whole reason for the change, and the
connected-provider path never had the problem because the store format always bound its entries to
a host.

Consequences worth knowing:

- **A PAT needs a host, and storing one without it is refused.** The host is what the credential is
  scoped to. Defaulting to the public instance would be silently wrong twice for a self-hosted
  GitLab or GitHub: the clone gets no credential for the host it actually uses, and `glab`/`gh` end
  up configured for a host the user never named. The mechanism this replaced worked against any
  host, so a quiet default would have turned a working self-hosted setup into a broken one. A token
  stored before hosts existed still migrates with the public instance as its host (see the list
  section below) rather than blocking unrelated credential updates.
- **The raw token no longer reaches the pod.** `gitlab_token` and `github_token` are not projected
  into `/secrets/creds` any more. The only form a PAT takes inside a session is a store line, so
  there is no file the agent can read and replay against a server of its choosing.
- **A PAT alone now produces a store.** Previously a user with no connected provider got no
  `gitcreds` secret at all and depended entirely on the helper. This is also what makes a manual
  PAT work for GitHub, which had no HTTPS route before: there was a `gitlabToken` field and no
  GitHub equivalent.
- **`gh` and `glab` are configured for free.** `setup-cli-auth.sh` already derived their config
  from the store, keyed on the user part of each line — which is why `ManualGitCredentials` writes
  exactly the `oauth2` and `x-access-token` values `GitProviderConfig.GitCredUser` uses. That also
  retired the `GITLAB_TOKEN` export that existed to authorize `glab` for manual PATs, and with it
  a token that every subprocess in the session could read out of the environment.
- **A connected provider still wins.** Git's store helper answers with the first entry matching the
  host, and OAuth lines are written first. A user who both connected a provider and stored a PAT
  for the same host keeps the behaviour they had, including automatic refresh.

Tokens are still scoped to the repositories actually requested: `BuildCredentialStoreAsync` emits a
line only for a provider some repository in the session names. A manual PAT is the exception — the
user stored one token for one host, so it is included whether or not a repository uses it, which is
what it did before. Which PATs a session gets at all is now a per-session choice (`gitPatIds` on
create, update and duplicate; null is the default token of each host), see `docs/credential-scopes.md`.

## A list of tokens keyed by host, not one slot per provider

The secret used to hold exactly one GitLab PAT and one GitHub PAT (`gitlab_token`/`gitlab_host`,
`github_token`/`github_host`). That shape cannot express the common case it was built for: a
company GitLab *and* a personal one, or a GitHub Enterprise host next to the public instance. A
user with two hosts had to pick, and the merge-style `PUT /api/credentials` had no way to say
"a second one".

Tokens are now a JSON list in a single secret key, `git_pats`:

```json
[{"id":"…","kind":"gitlab","host":"gitlab.example.com","token":"…"},
 {"id":"…","kind":"github","host":"github.your-org.example","token":"…"}]
```

with their own endpoints — `POST /api/credentials/git-pats {kind, host, token}` and
`DELETE /api/credentials/git-pats/{id}` — and `GET /api/credentials` listing `gitPats` as
`{id, kind, host, label, isDefault, authorName, authorEmail}` — never the token. The `PUT` no longer accepts the four legacy fields.

- **Upsert per host, not append.** A `POST` for a host that is already stored replaces that
  entry's token and keeps its id. (Since a host can carry several tokens, host *and label* pick the
  entry — see "Several tokens per host" below; for a client that sends no label nothing changed.) The alternative, appending and letting the UI sort it out, fails
  in git itself: the store helper answers with the *first* line matching a host, so a rotation that
  appended would leave the stale token winning until the user found and removed the old line. The
  host is the identity because it is what the credential is scoped to; the kind only decides the
  user part of the store line (`oauth2` vs `x-access-token`), which is what `setup-cli-auth.sh`
  keys on to configure `glab` or `gh`.
- **Hosts are compared case-insensitively and stored lower-cased**, so `GitLab.Example.com` and
  `gitlab.example.com` cannot become two entries that git would treat as one host.
- **Legacy slots migrate lazily.** Reading the secret folds `gitlab_*`/`github_*` into the list in
  memory; the next write of the secret — any credential save, a token add or remove — writes the
  list and drops the slots. Reading never writes, so a user who only looks at their status page
  keeps a secret an older backend still understands. The migrated entries get fixed ids
  (`legacy-gitlab`, `legacy-github`) rather than random ones: the status page shows ids before
  any write has happened, and a `DELETE` for a random id would miss on the next read. A legacy
  token stored before hosts existed migrates with the public instance as its host, because that
  is the host it was used against.
- **Bounded at 32 entries.** The whole list is one key of a secret capped at 1 MiB; without a bound
  a scripted caller could grow it until every credential write for that user fails.
- **`glab` gets a default host.** Both CLIs keep one entry per host and pick the host from the git
  remote they run in. Outside a repository `glab` falls back to `GITLAB_HOST`, then the top-level
  `host:` key of its config, then the public instance — so `setup-cli-auth.sh` sets `host:` to the
  first GitLab line in the store (connected providers come first). Without it a user whose only
  GitLab is self-hosted would have `glab` talk to a host they never named. `gh` has no equivalent
  config key, only `GH_HOST`, so it keeps its own default there.

The credentials page mirrors the model: a list of stored hosts with a kind badge and a remove
action that confirms inline (first click arms, second removes — no browser popup), and one inline
row to add a token. Adds and removes apply immediately rather than on Save: the list is keyed by
host, so the user needs to see the result of each add on its own, and a token must not sit in a
form field waiting on an unrelated Save that may never come.

## Several tokens per host

A shared session usually should not push under its owner's personal token. The work belongs to a
group or a project, and GitLab and GitHub both issue tokens for exactly that (group and project
access tokens, fine-grained tokens on a bot account). With the list keyed by host there was no room
for one: a second token for `gitlab.example.com` replaced the first.

The list now holds any number of tokens per host. Each entry gains three optional fields — a
`label` the owner chooses ("personal", "group: platform"), and a commit `authorName`/`authorEmail` —
and a `default` flag, of which every host has exactly one.

- **What a request means.** `POST /api/credentials/git-pats` with an `id` edits that entry (label,
  author, `default: true`) and needs no token. Without an id, an entry with the same host *and*
  label is rotated in place — so a client that never sends a label still rotates the host's token
  as before — and anything else is a new entry. Kind and host of an id are fixed: a session's
  choice is judged per host, and an id that moved to another host would take every session that
  picked it along.
- **A session uses at most one token per host.** Git's store answers with the first line matching
  a host, so two tokens for one host in a pod would make the choice depend on list order. The
  selection (`gitPatIds`, `docs/credential-scopes.md`) refuses two ids for one host with a 400, and
  the spawn keeps the first should an older stored selection still name two. `null` now means "the
  default of every host" — the same set "every token" was while there was one per host.
- **Migration is a read, not a write.** An entry written before this has no `default` flag; the
  first entry of a host without a marked default is its default. Every existing entry was alone on
  its host, so each keeps its id, becomes its host's default, and a session that never chose keeps
  exactly the token it had. Removing a default hands the role to the next token of that host and
  writes it down. `default: false` is ignored rather than leaving a host without one — the way off
  a default is to mark another token.
- **A removed chosen token is not replaced by the default.** A session pointed at a group token
  whose token was deleted gets no token for that host, not the owner's. Falling back would quietly
  turn shared work back into the owner's pushes, the one thing the choice exists to prevent.

Rejected: letting one session hold several tokens for a host, picked per repository with
`credential.useHttpPath`. That changes how git looks up *every* line in the store, the
connected-provider ones included, and `gh`/`glab` keep one token per host anyway, so the CLIs would
still act under one identity while git pushed under another.

### The commit author follows the token

A group token pushing commits that carry the owner's name is the same confusion one level down, so
a token can carry the author its commits use. The author is per host, not global: a session can use
a personal token on one host and a project bot on another. Git has no conditional `[user]`
section, only conditional includes, so `common/git-identity.js` writes one small file per host with
its `[user]` and one include file of

```ini
[includeIf "hasconfig:remote.*.url:https://gitlab.example.com/**"]
	path = "/home/agent/.config/agenthub/git-authors/0.gitconfig"
```

(plus the `ssh://` and scp-style forms for hosts without a port), included from `~/.gitconfig` after
the owner's own `user.name`/`user.email`, so for a matching remote the token's author wins.
`hasconfig:remote.*.url` (git 2.36) follows the repository's remote, which is what the token is
chosen by. The alternatives each miss a case: one global `user.name` is wrong as soon as two hosts
are in use; writing the author into each clone's `.git/config` misses every repository the agent
clones later; `includeIf "gitdir:"` follows a path the agent chooses, not the host it pushes to.
Measured against git 2.39: a remote on `gitlab.example.com.evil.example` does not match the
`gitlab.example.com` pattern, and neither does another host.

A host the connected provider covers gets no PAT author: its OAuth line is first in the store and is
what pushes authenticate with, so the PAT's identity would claim an author the push does not use.
The authors reach the pod as the `authors` key of the `gitcreds` secret, next to `credentials`.
Name and e-mail are refused when they contain quotes, backslashes, `<`, `>` or control characters —
they end up as quoted git config values and in every commit's `Name <email>` line.

### Switching the token of a running session

`PATCH /api/sessions/{id}/git-pats {gitPatIds}` stores the selection and, for a running pod, sends
the rebuilt store and authors to `PUT /agenthub/git-credentials` on the session agent, which
rewrites `~/.git-credentials`, the author files and the `gh`/`glab` configs. No restart: unlike a
provider login (`docs/provider-accounts.md`), git and both CLIs read their files on every command,
so the next push already uses the new token. The answer's `appliedNow` is false when the pod cannot
take it — not running, or an image from before the route (its 404) — and the dialog then says
"applies at the next start". A failed push to the pod is logged, not returned as an error: the
selection is saved either way, and an error would tell the owner nothing changed when it did.

The `gitcreds` secret is rewritten as well, because a container restart within the same pod copies
the store from it again and must not bring the previous token back. The CLI configs are deleted
before they are derived again: `setup-cli-auth.sh` only writes them when the store has lines, so a
switch to "no token for this host" would otherwise leave the old token in `glab`'s config.

The share dialog is where the choice is offered, because that is when it matters: it shows "pushes
as `<label>` on `<host>`" for each token in use and lets the owner move the session to another
stored token before sending the invitation. Collaborators never see a token, and only the owner can
open the dialog.

Rolling the backend back to a version from before this keeps working with one caveat: the older
reader ignores `label` and `default` and puts every token of a host into the store, so the first one
stored wins for that host until the extra tokens are removed.

## Renewing a connected provider's token

A session used to get its connected-provider token once, when it started, and the pod kept it for
as long as it ran. GitLab's OAuth access tokens live two hours, so any longer session ended up
pushing with a dead one (issue #289). Reproduced against a stand-in GitLab with 60-second tokens
and real git over HTTPS through the pod's store (`tests/GitOAuthRefreshProof/`): the first command
after expiry fails with `Authentication failed`, git's store helper then **erases** the line it
just saw rejected, and every command after that fails with `could not read Username` — for the
rest of the session, a new sign-in included, because nothing ever wrote the store again.

Three more faults sat on the same path:

- **The renewal raced.** The only renewal was on demand (a session start, a project search) and
  took no lock. GitLab rotates the refresh token and accepts each one once, so when several callers
  found the token due at the same time, one renewed it and the rest were answered `invalid_grant`
  and handed out the old access token, which that renewal had just made worthless. Measured: three
  session starts and six searches at once gave 8 `invalid_grant`, five empty searches and three
  sessions holding a revoked token.
- **A write could drop the only live refresh token.** The secret was replaced with the resource
  version of an earlier read, so a concurrent write made the replace fail with 409 after the
  provider had already rotated the token. The new refresh token was then lost, and the stored one
  was dead.
- **A refused grant was silent.** A revoked application kept showing "connected", and sessions
  kept getting the dead token.

What it does now:

- **The hub renews ahead of expiry, under one lock per owner and provider.** `GitOAuthRefreshService`
  renews every grant a running session uses once it expires within `AgentHub:GitOAuthRefresh:Margin`
  (15 minutes; a pass every `Interval`, 1 minute). The lock is the Postgres advisory lock the Claude
  refresher uses (`git-oauth/<owner>/<provider>`), so two replicas, a pass and a session start all
  take turns; whoever gets it second re-reads and finds the token fresh. A session start that finds
  the lock held waits for it (`LockWait`) rather than spending the token a second time. The same
  burst as above: one renewal, no `invalid_grant`, every caller got the new token.
- **The secret is written by compare-and-swap.** A conflicting write is re-read and retried, and a
  renewal is stored only while the secret still holds the refresh token it spent. A sign-in in the
  meantime is the owner's newer choice and wins.
- **Every change reaches the running sessions without a restart.** Renewed, refused, signed in
  again or disconnected: each raises a change that rebuilds the store of every running session of
  that owner whose repositories name the provider, and installs it through the same
  `PUT /agenthub/git-credentials` the token switch uses (above), plus the `gitcreds` secret for a
  container restart. A pod that could not be reached is retried on every pass. Measured: 200
  seconds of fetch and push across five renewals without a failure, and after a revocation and a
  new sign-in the same pod pushed again.
- **A refused grant is a state, not a silent failure.** Only `invalid_grant` on renewal marks the
  grant `.rejected`. `invalid_client` or a 5xx points at the app's configuration or a provider
  outage, which a new sign-in would not fix, so those are retried. A refused grant gives the session
  no line for that host rather than the dead token, `GET /api/git/providers` reports `needsSignIn`,
  the account page and the session workspace say "sign in to GitLab again", and the sign-in clears it.

Grants no running session uses are not renewed in the background: a session start renews on demand,
and GitLab's refresh tokens do not lapse from disuse. The Claude refresher renews every login
because Claude's refresh tokens do lapse (`docs/provider-accounts.md`); doing the same here would
spend a provider request on every connected owner every two hours for nothing.

Rejected: a credential helper in the pod that asks the hub for the token on every git command. It
would make expiry impossible by construction, but it brings back exactly what the first section
removed — a helper that hands out a token on request — and puts the hub on the path of every git
command, so a hub restart would fail clones. Pushing the store on change keeps git reading a plain
file.

### 401, 403, and what the owner sees

Against gitlab.com, an invalid OAuth token is `401` on git over HTTPS (`HTTP Basic: Access denied
… either incorrect, expired, or improperly scoped`) and `401 invalid_token` on the API. Expiry is
therefore a 401, not a 403. A 403 has two other sources: a token whose scope does not allow the
call (`read_repository` without `write_repository` cannot push; the default `api` scope can), and
GitLab's failed-authentication ban for git over HTTP (`rack_attack.git_basic_auth` in `gitlab.yml`,
or the rate limit on GitLab.com), which answers 403 to *every* request from an address after
repeated failed logins. Session pods usually leave the cluster through one address, so sessions
retrying a dead token can get the whole cluster banned, sessions with a valid token included. That
is why a refused grant takes the line out of the store instead of leaving it there. The stand-in
models the ban from the documented behaviour; it was not measured against a real instance.

What remains: the stand-in revokes the previous access token the moment it renews, the strictest
reading. A git command whose requests straddle that moment fails once (seen once in five renewals
in one run); the next command already has the new token. Renewing a quarter of an hour early does
not change that window, which is the time between the provider's answer and the pod's install —
milliseconds on the measured path.

## Validation, and why it is at the edges

Two things used to fail silently, both ending as a dead session with the reason buried in an
init-container log an API caller never sees.

**An unknown or unconnected `providerId`.** `BuildCredentialStoreAsync` skips a provider it cannot
resolve and skips one the user has not connected. No credential, no mount, and a clone that fails on
authentication. This mattered little while the only producers were a picker in the browser and a
chat command — both only ever emit ids that exist. An API or MCP caller composes the list itself, so
the id is now checked when the session is created, and an unconnected provider says so.

**An SSH URL without a stored known_hosts entry.** Host key checking is enforced on purpose
(`StrictHostKeyChecking=yes`), so an SSH clone without one cannot succeed. Create now rejects it,
and separately rejects an SSH URL with no stored key at all.

A PAT is validated where it is stored rather than where it is used. The store is line-based, so a
token containing a newline would append an entry for an attacker-chosen host to the user's own
credential file; a host must be a hostname with an optional port, nothing richer. Validating at
store time means the user hears about it while they are looking at the field.

## Repository list limits

None of this was bounded before. The only cap anywhere was `.max(32)` in the stdio MCP server's
schema, which the REST API does not go through — so it described nothing the server enforced.

- **16 repositories.** Each is one clone in a single init container, run in sequence. The cap is
  about the failure past it: a list of several hundred either runs for hours or is rejected by the
  Kubernetes API for pod-spec size, and both look like a session that never starts.
- **Transports: https, http, ssh, git, and scp-style `user@host:path`.** `ext::` is excluded because
  it runs an arbitrary command as a transport helper, and `file://` because it reads paths inside
  the pod rather than a repository. A URL may not begin with `-`, which would reach `git clone` as
  an option — `--upload-pack=` is git's own, and the script quoting the URL does not help.
- URL and branch length caps, so an oversized value fails validation rather than the Kubernetes API.

The same checks run on create and on update, because an edited list takes effect at the next start:
without them, editing a session could leave it unable to come back.

## What the API and MCP surfaces accept

`POST /api/remote/sessions` already took the full `repos` array including `providerId`. The two MCP
surfaces did not, which is the gap that actually blocked API-created sessions from using a connected
provider:

- The remote MCP tool took a single `repoUrl`/`repoBranch` and no provider. It now takes `repos` as
  **JSON text** — not a declared array — for the same reason the boolean flags are strings: an MCP
  client caches the tool schema when it connects, so a newly declared array type makes every
  already-connected client's call fail until it reconnects. Malformed JSON is reported rather than
  ignored; silently creating a session with an empty workspace is worse than an error.
- The stdio server accepted `repos` but had no `providerId` in its schema, so the field was stripped
  before the HTTP call. It is now in the schema and in `sanitize.mjs`, so a caller can read back how
  the session was authenticated. The id is not a secret; the token never leaves the backend.

- The in-session server (`agent-runtime/sessions`, the one an agent uses to create a child) was
  missed by that fix and stripped `providerId` the same way. It showed late because nothing fails
  at create: a child clones a public repository anonymously, works, and only then cannot push —
  `could not read Username`, hours in, with no credential in the pod to explain it. The field is
  now in its schema too.

  The schema alone would not have been enough here, because the caller is an agent and does not
  know a provider id; it knows a clone url. So `POST /internal/sessions/{id}/spawn` fills it in:
  a child repository without a provider gets the one its parent uses for the same https host. The
  alternative was matching the host against every provider the owner has connected, for every
  create. That was rejected because it turns "this url is on a host you connected" into "this
  session holds your token" for sessions that never asked — a parent that already carries the
  provider is the narrower statement of intent. A repository naming its own provider keeps it, an
  SSH remote is left to the stored key, and a host the parent has no provider for stays anonymous.

Validation lives in the session service rather than in any schema, so the rules cannot drift
between the MCP servers and the REST API.

## Not built

- **Credentials passed in the API call.** Everything above is reachable through a connected provider
  or a stored PAT, and this is the only route needing new secret handling. The leak surface is the
  problem: a credential embedded in a clone URL would land in the `REPOS` environment variable of
  the pod spec, in the `ReposJson` column verbatim, in the `GET /sessions` response, in the
  init-container log that echoes each URL, and in `.git/config` of the clone for the session's life.
  If it is ever built it should be a write-only field dropped before `ReposJson` is serialized and
  before `SessionInfo` is built, materialized only into the `gitcreds` secret.
- **Listing the repositories reachable through a connected provider.** `SearchProjectsAsync` exists
  and has two consumers, but `GET /api/git/projects` is behind the interactive auth pipeline while
  the remote API resolves a personal API token on a separate path, so a token cannot reach it. Worth
  exposing — it needs a short per-owner cache first, since nothing caches today and an agent
  resolving several repositories would hit the provider's rate limit.
- **Resolving a repository by name** rather than by clone URL. `ChatRepoService` already does this
  for chat-created sessions; the logic should be extracted and shared rather than copied, keeping
  its fail-closed behaviour — an ambiguous name must be an error, never a guess.

## Still open, and deliberately untouched here

The state tar does not carry git credentials out, but only incidentally: it archives the agent's
state directory, and `~/.git-credentials`, `~/.ssh/id` and the `gh`/`glab` configs all sit outside
it. A driver that widened `stateDir` to `$HOME` would start uploading git tokens to object storage
on every session end. An explicit exclusion and a test would make that a property rather than an
accident.
