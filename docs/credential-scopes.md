# Which credentials a session gets, and which ones a token may hand out

A session used to take *everything* the owner had stored: the default provider login (or the one
named in `credentialId`), and every git personal access token in the credential secret. A personal
API token (`oah_…`) could create sessions with all of it, because a token *was* its owner. This file
records the two changes that split that up — a per-session choice of git tokens, and a per-token
restriction on which logins and git tokens a session created with it may use — and the
alternatives each one rejected.

## Git tokens per session

`CreateSessionRequest.GitPatIds` names which stored PATs the session's `~/.git-credentials` is built
from. The same field exists on `UpdateSessionRequest` (applies at the next start or resume, like
every other runtime field) and on `DuplicateSessionRequest`; `SessionInfo.GitPatIds` reports it.
Three values mean three different things:

| Value | Meaning | Stored as |
|---|---|---|
| omitted / `null` | every stored PAT, including ones added later — what every session did before | `NULL` |
| `[]` | no PAT at all; connected providers and the SSH key still apply | `[]` |
| `["id1", "id2"]` | exactly these; an unknown id is a 400 at create or update time | `["id1","id2"]` |

`["*"]` is accepted as a spelling of "all" wherever the field is written, because a PATCH has no
other way to go *back* to "all" once a list has been stored: the update convention is null =
unchanged, and an empty list already means "none". The wildcard is the same token the API-token
scope uses below, so there is one spelling for "everything" rather than two.

The store line is built from the selection at spawn time, from the record rather than from the
request — a resume rebuilds the request from the record anyway, and reading the record in one place
means a session cannot be resumed with a different selection than it was created with. A selected id
whose PAT has since been removed is skipped, not an error: a PAT is one of possibly several
credentials for a clone, and `ManualGitCredentials` already skips an entry it cannot use rather than
stopping the session over it. (A *pinned provider account* that was removed does fail the session —
there is nothing else to authenticate the agent with, so silence there would hide the one thing that
matters.)

**SSH keys and connected providers are deliberately not part of this.** The SSH key is a single value
per user with no identity to select by; making it selectable would mean inventing an id for a
one-slot field. Connected OAuth providers are already chosen per repository through
`RepoRef.ProviderId`, which is a finer-grained selection than a per-session list would be. PATs
were the only credential a session received in bulk, with no way to say "not that one".

The alternative was no selection at all, which is the status quo, and the failure it allows is the
reason to change it: a user with a company GitLab token and a personal one cannot start a session
that holds only the personal token. Every clone target that answers 401 on the company host would be
offered the company token — by git's own host matching, so only for that host, but still without the
user having chosen it for this session.

## The account dropdown shows up from one account

`AgentDecisionCard` used to hide the account choice until the owner had two logins of the chosen
agent, on the grounds that one login needs no choice. True, but it also meant the dialog never said
*which* login the session would run on, and once a token restriction (below) can forbid the default
account, "the default" is no longer something the user can take for granted. The dropdown now shows
from the first account, with its label and identity, and preselects the default. A session created
from the dialog is therefore pinned to the account shown, which is what already happened with two
accounts; sessions created without a `credentialId` by an API caller still follow the default.

## Listing credentials through the remote API and MCP

`GET /api/remote/credentials` and the `credentials_list` tool answer what the caller can pick from:

```json
{
  "accounts": { "Claude": [ {"id": "…", "label": "Work", "email": "…", "isDefault": true } ], "Codex": [] },
  "gitPats": [ {"id": "…", "kind": "gitlab", "host": "gitlab.example.com"} ],
  "apiKeys": { "anthropic": true, "openai": false, "cursor": false }
}
```

The account map is keyed exactly like `GET /api/credentials/accounts` (`Claude`, `Codex`, …),
because the ids and labels come from the same listing and a client should not have to learn two
spellings of one provider. Only what the token is allowed to use is listed — a restricted token sees
its own slice, not the owner's whole credential store, so a leaked restricted token tells its holder
nothing about logins it cannot use anyway.

The MCP `session_create` tools take `credentialId` and `gitPatIds`, the latter as a comma-separated
string rather than a declared array. The reason is the same as for the boolean flags
(`docs/provider-accounts.md`): an MCP client caches the tool schema at connect time, and a parameter
that arrives as a string from an already-connected client would fail JSON binding against an array
type until that client reconnected.

## Restricting a personal API token to some credentials

`api_tokens.allowed_credentials` holds a JSON document, or `NULL` for a token that may use everything
(which is every token that existed before this column, so nothing changes for them):

```json
{
  "providerAccounts": { "claude": ["3f9a1c2b7e0d4a61"], "codex": ["*"] },
  "gitPats": ["d3b07384d113edec49eaa6238ad5ff00"],
  "apiKeys": false
}
```

It is an allow list in every part. A provider that is not named may not be used; `["*"]` allows any
of its accounts, including ones added later; a `gitPats` entry that is missing means no PAT, and
`apiKeys` missing means no API-key session. The alternative — treating a missing part as "no
restriction on that part" — reads more leniently but fails the wrong way: a user who restricts a
token to one Claude account, and says nothing about git, would hand that token every PAT they own
without having been asked. With an allow list, the restricted token can only lose capabilities by
being written, never gain them by being written incompletely.

### What happens when a restricted token creates a session

Token resolution returns a `RemoteCaller(owner, scope)` rather than a bare owner string, and
`POST /api/remote/sessions` runs the request through `CredentialScope.ApplyToCreate` before the
session service sees it. The service itself is unchanged and still validates ids against the store;
the scope only narrows what reaches it.

- **Subscription session with `credentialId`**: the id must be allowed for the agent, otherwise
  `403 credential_not_allowed`.
- **Subscription session without `credentialId`**: if the owner's default account for that agent is
  allowed, nothing changes and the session follows the default. Otherwise, if exactly one allowed
  account exists, the request is pinned to it — a token restricted to "the CI account" should not
  have to repeat that id in every call. Otherwise `403 credential_required`: guessing between two
  allowed accounts would start work on a login the caller may not have meant.
- **An agent the scope does not name**: `403 agent_not_allowed`.
- **API-key session**: only with `"apiKeys": true`, otherwise `403 api_keys_not_allowed`.
- **Git**: a request that names PATs may only name allowed ones (`403 git_pat_not_allowed`); a
  request that says nothing gets the allowed set intersected with what is stored. With `["*"]` it
  stays "all", so a PAT stored later still reaches the session, as the unrestricted behaviour would.

The error codes are the body of the 403, so an MCP client gets a stable word to act on rather than a
sentence to parse.

### Why nothing is re-checked on resume, pause or delete

Only `POST /api/remote/sessions` and the listing consult the scope. A resume takes the credential
recorded on the session. The scope is a property of the *token*, the session belongs to the *owner*,
and the owner can edit the session's account in the web app at any time — a check at resume time
would have to decide whether an account the owner chose by hand is "allowed" for a token that never
chose it, and either answer is wrong for someone. The restriction is about what a token can bring
into existence, not about what it may touch afterwards. The alternative, binding every session to the
token that created it, would also have made a session unusable the moment that token was deleted,
which is not what deleting a token means.

### Validation at the edge

`POST /api/tokens` and `PATCH /api/tokens/{id}` accept `allowedCredentials` and check every id
against the owner's accounts and PATs at that moment. An unknown id is a 400 that names it, rather
than a scope that silently allows nothing because it points at a login that was renamed or removed.
The `"*"` entries are kept as written. The validation lives in `ApiTokenScope.Normalize`, a pure
function, so the controller stays thin and the rules are tested without a database. Agent names in
`providerAccounts` are matched case-insensitively and stored as the canonical `AgentKind` name, so
`claude` and `Claude` do not become two different restrictions.

## What is deliberately not done

- **No scope on which sessions a token may read, pause or delete.** The token still acts as its owner
  for everything except credential selection. A full capability model for tokens is a different
  feature; this one answers "which login does a session started by this token run on".
- **No per-session choice of SSH key or API key.** Both are single values per user, see above.
- **No expiry on tokens.** It was listed as missing alongside scopes, and it is still missing; it is
  orthogonal and would deserve its own column and sweep.
