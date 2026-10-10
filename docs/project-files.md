# Reading the files of other sessions in a project

Session files were strictly per session. A callback token opened the files of its own session
and nothing else, the in-pod `agenthub_files` MCP server knew one session, and no API listed a
file under any id but its session's. That is the right default, and it made one ordinary
arrangement impossible: a session that produces something — screenshots, a report, an export —
for other sessions of the same project to use. The only way across was a person downloading the
file from one session and uploading it into the next.

A session can now **read** the files of the other sessions that have the **same owner and the
same project**. This file records the rule, where it is enforced, and what was decided against.

## The rule

A session `A` may list and download the ready files of a session `B` when all of these hold:

- `A` and `B` have the same owner,
- `A` and `B` have the same project, and that project is set,
- `B` is not `A`,
- `A` is not shared with anybody (see "Shared sessions" below).

Nothing else is granted. Uploading, deleting and presenting stay limited to the session's own
files; there is no route under the project prefix that writes, so this is not a check that could
be forgotten on one of them.

**A session without a project has no siblings.** "No project" is not treated as a project of its
own. Doing so would pool every session an owner never sorted, which is the opposite of what
leaving a session out of a project says.

The rule lives in one class, `backend/Files/ProjectFileAccess.cs`, and both routes ask it. The
alternative was a check in each route. It was rejected because a listing and a download that
each carry their own copy can drift, and the direction that matters is a download serving a file
the listing would never have shown.

`ProjectFileAccess.IsSibling` compares the owner even though both of its lookups are already
scoped to the caller's owner in SQL. The comparison is the boundary; the query is an
optimisation. The tests run against a store that deliberately ignores the owner argument, so
they pass because the rule refused and not because a query happened to be narrow.

## Automatic for the project, not opt-in

Every ready file of a sibling session is readable. There is no flag on a file and none on a
session.

**The alternative was opt-in — a "share with project" mark per file, or per session.** It was
rejected for three reasons:

- *The boundary it would add is not a boundary.* Both sessions belong to the same person, who
  can already open either one and move any file between them by hand. An opt-in would protect
  the owner from themselves, at the cost of the feature working only when somebody remembered
  the mark.
- *The producer is an agent.* A per-file flag has to be set by the session that makes the file,
  which means a parameter on `upload_file` that the producing agent must know to pass. The
  sessions this is for — an unattended one taking screenshots — are exactly the ones nobody is
  watching to notice it was left out. A missing flag fails silently: the consumer sees an empty
  listing and cannot tell "nothing was made" from "nothing was marked".
- *A project is already the grouping.* Sessions of one project already see each other in the
  agent directory and can message each other. Putting a session into a project is the opt-in;
  a second switch for the same decision would have to be explained everywhere the first one is.

What automatic sharing costs: a session cannot keep a file from its project siblings. Whoever
needs that keeps the session out of the project — the same answer as for the agent directory.

One consequence is worth stating plainly. An agent that is led astray — by a prompt injected
through a web page or a repository — can read the files of its project siblings where before it
could read only its own. It could already message those siblings and it runs with the same
owner's credentials, so this widens what such an agent can *read*, not whom it can act as. A
project that mixes sessions handling untrusted input with sessions holding sensitive files
should be two projects.

## Shared sessions (Enterprise)

A share — a direct grant or a link — is given for **one** session. A collaborator types into
that session's terminal; a viewer watches it. If the agent there could fetch a sibling's file,
the recipient of the share would get at files from sessions that were never shared with them:
the collaborator by asking the agent, the viewer by watching what it prints.

So **a session that is shared with anybody reads no project files at all.** The listing is
empty and every download is the usual 404. Its own files are unaffected, and so is the other
direction — an unshared sibling can still read the shared session's files, because that gives
the share's recipient nothing.

Decided against:

- **Refusing only when a collaborator exists.** A viewer cannot make the agent fetch anything,
  so this would be enough for the active case. It was rejected because it puts a role into the
  rule: a viewer upgraded to collaborator, or an autonomous agent fetching and printing on its
  own initiative while a viewer watches, would both need their own reasoning. "Any share" has no
  role to get wrong.
- **Leaving it to the MCP sharing policy.** An owner can already block individual MCP tools for
  a shared session. That is opt-out — the default would leak — and it lives in the agent's hook,
  whereas this is refused by the backend whatever the runtime does.
- **Checking the share when the pod starts.** The check runs on every request
  (`ISessionShareStatus.IsSharedAsync`), so sharing a session closes project access for the pod
  that is already running, and revoking the share reopens it.

A share counts while it exists: a direct share of either role, or a link that has not expired.
An expired link opens nothing and does not count. Whether a licence is currently active is
deliberately *not* part of the question — without one the share is inert, but the licence can
return without anyone looking at the session again.

What this does not undo: a file the session fetched *before* it was shared is in its workspace
like anything else the owner put there. Sharing a session shares its workspace; that was true
before this feature.

The shared-link and collaborator routes (`api/shared/{token}/files`, `api/sessions/{id}/files`)
are untouched. They resolve exactly one session and have no project variant, so a share's
recipient has no route to a sibling's files of their own either.

## The routes

Both are internal, authorised by the caller's own callback token (`X-Agent-Token`), under the
caller's own session:

| Route | Answer |
|---|---|
| `GET internal/sessions/{id}/files/project` | `{ files: [...], truncated }` — one entry per ready file of every sibling, with `sessionId` and `sessionTitle` |
| `GET internal/sessions/{id}/files/project?sessionId=…` | the same, for one sibling |
| `GET internal/sessions/{id}/files/project/{sessionId}/{fileId}/content` | the bytes |

The caller is whoever the token says. The sibling's id in the path is looked up on the server,
from the caller's stored owner and project; nothing the request carries can widen that.

**The alternative was to let a callback token open a sibling's existing routes** — teach
`AgentCallbackAuthorizer` that a token is also good for `internal/sessions/{sibling}/files`. It
was rejected because that authoriser guards every internal route of a session: reserve, upload,
complete, present, and beyond files the permission, notify and account routes. Read-only would
then be a property each of those had to re-establish for the "foreign token" case, and the first
one that did not would let a session write into its neighbour. Separate routes that can only
read make the limit structural.

### One answer for every refusal

An unknown session, another owner's, another project's, a caller without a project, a shared
caller, the caller's own id, an unknown file: all are a bare `404`, with the same body. A `403`
for "exists but not yours" would let a session probe which ids exist outside its project, and so
would a `404` that carried an error body in one case and none in another — the controller
therefore turns the file service's `file_not_found` into the same empty `NotFound` instead of
passing its body through.

A missing or wrong token is `401`, as on every internal route. That reveals nothing: it is the
answer before any session has been looked at.

The download goes through the API and never redirects to object storage, for the reason the
session's own content route gives: the pod's fetch would follow the redirect carrying its
callback token.

### The listing is capped

A project of long-lived sessions can hold more file rows than the in-pod client accepts in one
response (one megabyte). Past 500 entries the listing stops and says `truncated: true`; the
caller narrows it with `sessionId`. One session holds at most `MaxSessionFiles` (200), so a
narrowed listing is always complete. Failing the whole call on size was the alternative, and it
would have made the feature stop working exactly in the projects that use it most.

## The MCP tools

The in-pod `agenthub_files` server has two new tools:

- `list_project_files { sessionId? }` — the sibling files, each with its source session's id
  and title.
- `fetch_project_file { sessionId, fileId }` — copies the file into the managed directory in the
  session's workspace and returns `localPath`.

**The alternative was a parameter on `list_files` and `read_file`.** Rejected twice over. A
connected client caches a tool's input schema, so a parameter added to an existing tool is
invisible to every session already running (`development-log.md`, 2026-09-29). And it would have
changed what the existing tools mean: `list_files` answers "what does this session have", and
keeping that answer exact matters more than saving two tool names. Both existing tools reject
the new arguments; a test pins that.

`fetch_project_file` never returns content inline, unlike `read_file`, which inlines small
images and text. The point of sharing between sessions is to hand finished output from the
session that made it to the one that uses it, and that hand-over should cost a path, not the
bytes of every screenshot in the model's context. An agent that wants to look at one opens the
path.

The tool takes the file's name, type and size from the backend's listing rather than from its
caller. The size is the ceiling the download is cut off at, so it must not be something the
model supplies. An id that the listing does not contain never reaches the download route.

A `404` from the project routes reaches the agent as `file_not_found` and nothing more specific
— on purpose, see above. The session's own tools report errors as they always did.

## Where it was not added

**The web UI.** The files pane shows one session's files, keyed by file id, with its selection,
presentation state, live events and polling all scoped to that session. Showing sibling files
there needs a user-facing listing route with an owner check (the pane cannot currently tell an
owner from a collaborator, and a collaborator must not see them), a second selection space,
previews fetched under a different session id, and a refresh trigger from sessions the pane is
not subscribed to. That is not a small change, and the owner already has every one of those
files one click away in the session that made it. Left out.

**The token API and the stdio MCP server (`mcp/agenthub`).** Neither exposes session files at
all today — not even a session's own. An equivalent would not be "the same rule on one more
surface" but a new file surface for personal tokens, with its own questions (token scopes,
download size through a stdio server). And a personal token already acts as the owner across
all their sessions, so "same project" would not be the boundary there; it would be a filter.
Left out; the rule in `ProjectFileAccess` is where such a surface would ask if it is built.

## Proof

Besides the unit tests (`ProjectFilesTests`, and `ProjectFilesPostgresTests` against the real
session and share tables), the feature was run over the real request path: the shipped
`agent-runtime/files/server.mjs` started as a stdio MCP server per session, talking HTTP to a
host built from the shipped controller, authoriser, rule and file service on Postgres. Object
storage was the one stand-in. A file uploaded by one session was fetched by its project sibling
byte-identical onto disk; the same call against a session of another owner, of another project,
without a project, a deleted one, the caller itself and an id that never existed returned the
same `404` with the same body length; `PUT`, `POST` and `DELETE` on the project routes were
`405`; and a viewer link on the reading session closed its access until the link was revoked,
without restarting the MCP server.
