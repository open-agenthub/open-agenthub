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
what it did before.

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
`{id, kind, host}` only. The `PUT` no longer accepts the four legacy fields.

- **Upsert per host, not append.** A `POST` for a host that is already stored replaces that
  entry's token and keeps its id. The alternative, appending and letting the UI sort it out, fails
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

Validation lives in the session service rather than in either schema, so the rules cannot drift
between the two MCP servers and the REST API.

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
