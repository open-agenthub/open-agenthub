# Who can open a shared session, and where it shows up

A direct share gives one known user a role on one session; a link gives whoever holds it a role
on one session. This file records how those grants become access — for the session, for the
children its agent starts, and in the recipient's own session list — and the alternatives each
decision rejected. The HTTP and MCP surfaces that create the grants are in
`session-sharing-api.md`.

## One decision, many callers

Every surface that lets a non-owner in — the session page, terminal and file proxies, the
workspace diff, the token API, the MCP tools (#408) — asks `ISessionAccessService`, and the list
of sessions shared with someone is built by `ISessionSharingService` from the same rule. The
store (`SessionShareStore`) only *collects* the grants that could apply; which one wins is
`SessionAccessRules.Decide`, and nothing else decides it.

**The alternative was to let the store compute the effective role in SQL**, which is what the
single-session lookup did while there was only one possible grant per session. With inheritance
there are several, and the list query and the single lookup would each have had to repeat the
precedence in SQL. The first time one of them was changed without the other, a session would have
shown as Viewer in the list and opened as Collaborator, or the other way round. A test
(`StrongestGrantWins_WhereverItIsAttached`) checks that the two agree.

## Precedence

1. **Owner** beats everything. An owner never needs a grant and none can narrow theirs.
2. Otherwise **the strongest role among all grants that reach the session** applies:
   Collaborator over Viewer. A grant can only widen access, never narrow it.
3. Among equally strong grants, the most specific is reported as the reason (`inheritedFrom`,
   `sharedViaProject`): a grant on the session itself before an inherited one, a share on an
   ancestor before a project share, a nearer session before a farther one. This changes the
   label, never the level.

Grants that reach a session today:

| Scope | Source | Reaches |
|---|---|---|
| `Session` | direct share on the session | that session |
| `Session` | link on the session (token holder only) | that session, and nothing else |
| `Ancestor` | direct share on a session up the `parentSessionId` chain | every descendant |
| `Project` | share on a project, for a user or an IdP group (#409) | every session in the project, and their descendants |

**The alternative to "strongest wins" was "nearest wins"**: a child shared as Viewer under a parent
shared as Collaborator would open as Viewer. It was rejected because it makes a grant a deny. To
keep a collaborator out of one child, the owner would add a share that takes access away, and
revoking that share would silently give it back. With "strongest wins" every grant is additive,
and the only way to take access away is to remove a grant — which is what the share dialog
already shows.

## Sharing a whole project (#409)

The owner shares a project from its controls in the sidebar (⇪) with a user or a group, as Viewer
or Collaborator. The share opens every session in the project for that subject, including the
sessions started there later and the children of all of them. The dialog lists who has access,
and the session's own sharing dialog shows the project's shares beside its own, read-only.

It is one more scope, not a second mechanism: the store adds the project's grants for the
principal to the list it hands to `Decide`, and the precedence above applies unchanged. So **an
explicit share on one session can add to a project share, never reduce it**. Bob, Collaborator on
the project, stays Collaborator on a session shared with him as Viewer; Carol, Viewer on the
project, is Collaborator on the one session shared with her as Collaborator. The alternative, a
session share that overrides the project's role, was rejected for the reason "nearest wins" was:
it turns a share into a deny that silently lifts when it is revoked. Keeping someone out of one
session of a shared project means moving that session out of the project.

**Live, like the children.** Nothing is copied onto the sessions. Each access check looks up the
project of the session and of every ancestor in the same walk, so a new session opens on its
first request, a role change on the project reaches every session at once, and revoking the
share, moving a session to another project or deleting the project closes the session on the
next request. The alternative, writing a session share into every session of the project, has
all the faults of copying a parent's shares (see below), and it would also need a hook on every
way a session enters a project — creation, `session_create`, moving, the webhook trigger.

**Children inherit it through the ancestor rule.** A project grant found on an ancestor is
reported with `inheritedFrom` naming that ancestor, as a share on the ancestor would be. A child
started outside the project still opens; a child in the project is opened by the project share on
itself, which is the more specific reason. The list query does not follow a project grant into a
child of the same project, because that child is already listed in its own right.

**Within one owner.** A session's `project_id`, like its parent id, is whatever the creating
request named. A project grant therefore reaches only sessions whose owner owns the project, so
nobody can put a session into someone else's shared project and so show it to its recipients
(`OnlyTheOwnersSessionsInTheProject_AreReached`).

**Groups are the owner's own.** A group share names an IdP group (`user_groups`, refreshed at each
sign-in), and the owner can name only a group they belong to. The alternative, any group the IdP
has reported for anybody, would let every user test group names one by one and hand sessions to
groups they have no relation to. The dialog therefore offers the owner's groups and nothing else.
Membership is read on every request, so somebody who leaves the group loses access at their next
sign-in; a share stays in place if the owner leaves the group, because it was the owner's
decision when it was made.

**Rows go with their project and their people.** `project_shares.project_id` references
`projects(id) ON DELETE CASCADE`, as the session tables do since #368: deleting a project removes
its shares in the same statement, and a project id that is ever reused starts with none. The
account purge removes the shares on the account's projects and the shares naming the account as
recipient.

The same walk feeds `IsSharedAsync`, so a session in a shared project counts as shared for the
sibling-file rule (`project-files.md`). Every caller that asks `ISessionAccessService` — web,
proxies, token API and the MCP tools (#413) — gets project access with no change of its own. The
token API and the MCP tools cannot manage project shares yet; only the web app can.

## Children inherit live, not by copy (#410)

A session started by a shared session (`session_create` with `parentSessionId`) is opened by the
same people with the same role. The store walks the chain on every request: for one session it
collects the principal's shares on the session and its ancestors, and for the list it descends
from every session shared with the principal to all its descendants.

**The alternative was to copy the parent's shares onto the child when it is created.** It was
rejected because a copy outlives the decision it copied. Revoking the parent's share would leave
every child shared, a role change on the parent would not reach its children, and a share added
after the children started would not reach them at all. Deleting the parent would leave the
copies behind on the children as grants nobody remembers making. Live inheritance stores
nothing: removing the parent's share closes the children on the next request, and deleting the
parent (its shares go with it, `ON DELETE CASCADE`) closes them too, with no grant left over. The
owner keeps the children.

The cost is a recursive query per access check. It is bounded by `SessionDescent.MaxHops` (64),
the same limit every other chain walk uses, which also ends a corrupt cycle, and it follows the
`sessions` primary key upwards and `idx_sessions_parent (owner, parent_session_id)` downwards.

### The chain stays within one owner

A `parentSessionId` is whatever the creating request named. If the walk followed it across
owners, anybody could hang a session of their own under someone else's shared session and so show
it to that session's recipients. The walk therefore only steps to a parent or child with the same
owner; a session of another owner under a shared one inherits nothing
(`SessionOfAnotherOwner_UnderASharedOne_InheritsNothing`).

### What else follows the chain

- **The MCP sharing policy.** The policy a pod is held to is its own blocks plus those of every
  ancestor. Otherwise a collaborator could ask the parent's agent to start a child and use there
  the servers the owner blocked on the parent. The sharing dialog still shows and edits the
  session's own policy.
- **The sibling-file rule** (`project-files.md`): a session that somebody else can open reads no
  sibling project files. A direct share on an ancestor opens the child, so the child counts as
  shared.

## Links stay link-only

A link opens exactly the session it was made for, for whoever holds the token, and nothing else:
not the children (the token names one session, and a child started later was never part of what
was handed out), and not anybody's list (a link has no recipient to list it for). An ancestor's
link does not make a child "shared" for the sibling-file rule either.

## Where a shared session shows up (#407)

`GET /api/sessions/shared` returns every session a direct share or a project share opens for the
caller, including inherited children, in the sanitised shape of `SharedSessionInfo` with
`accessRole`, `sharedBy`, `inheritedFrom`, `sharedViaProject` (the project's name, shown as
"via project …") and — only when the caller can open the parent too — `parentSessionId`. The web
app merges it into the owner's list on every poll (every five seconds), so a new share appears
without a reload and a revoked one disappears on the next poll. In the sidebar and on the
Sessions page shared sessions form the group "Shared with me", children nested under their
parent and marked "child of …"; owner actions (pause, edit, share, delete, reorder) are not
offered.

A child whose parent the caller cannot open (it was shared directly, the parent was not) names no
parent, so the list never leaks the id of a session the caller cannot see.

### Counters, filters and alerts

The status filters treat a shared session like any other: "Question open" and "Waiting" find it
whatever the role, because the row shows that status. "Waiting on you" and the question alert
count it only for a collaborator: a viewer cannot answer, and a counter that a person can never
bring down by doing something is noise. A viewer is still alerted when the session finishes or
fails.

## Shared sessions on the home screen

Home lists all directly shared, inherited and project-shared sessions in a separate “Shared
with me” section with owner, role and status. Mixing them into the six-session owned preview
was rejected because recent owned sessions could hide every share. The existing access-role
rule still controls “Needs you”: collaborators can answer, viewers can see an open question
without increasing the waiting counter. The list follows the normal session refresh, including
revocations; it does not cache grants independently.

## The history a shared reader gets (#427)

Everyone a session is shared with — a named share, an inherited child, a project share, a link;
Viewer and Collaborator alike — reads the same conversation the owner reads, through one service,
`ISharedSessionHistory`. The web app's `/api/sessions/{id}/conversation` for a non-owner, the
link's `/api/shared/{token}/conversation`, the token API's shared branch and the MCP read tools of
#425/#426 all call it with the `SessionAccessResult` they resolved for that request; the service
never resolves again, so a revoked share or an expired link is refused by the same call that
refuses everything else, on the next request.

**Where the history comes from.** A running session is read live from its pod, as the owner's
Workspace is (`LiveTranscriptReader`); a stopped one from the stored copy. Before #427 the link and
token paths read only the stored copy, to spare a Kubernetes read per poll. That copy is two
pieces: the whole file in S3, written through a presigned url that expires after twelve hours,
and the last `ScrollbackLimits.MaxChars` (1,000,000 characters) in Postgres. Once the url had
expired, a long session's shared history was that Postgres tail — a few hours — while the owner
saw everything. The pod is now asked only when the *stored* status is Running, so a stopped
session still costs no Kubernetes read.

**Bounded pages.** At most 500 entries and about 1,000,000 characters per page (fewer entries,
`truncated: true`, `nextOffset` where it stopped; one oversized entry is still returned alone so
the cursor always moves), and at most 100,000 characters of scrollback. `offset: -1` keeps the
newest entries of the window; any other offset keeps the oldest, so following `nextOffset` has no
gaps. The owner's own page keeps its larger bound — the owner can download the whole state anyway.

**Sanitized like the shared status.** Session secrets are already masked by the pod before any
transcript leaves it (`secret-mask.js`, `session-secrets.md`). What the pod does not mask, because
they are not secrets in that sense, are the session's callback token and the inline MCP
configuration — which `SharedSessionInfo` withholds (`mcpConfigJson: null`). An agent that prints
its environment or its MCP config would otherwise hand a link holder the token that acts *as the
session* (`agent_send`, `secret_request`) and the owner's MCP credentials. Both are replaced with
`[redacted]` in entry text, tool lines, scrollback and the link's plain-text transcript. Header,
`env` and `url` values of the MCP config are withheld whole; server names, commands and arguments
stay readable. Values shorter than eight characters are left alone, for the reason `SecretMask`
gives.

**Rejected: a separate, pre-sanitized "shared transcript" written at upload time.** It would have
doubled the stored history and, worse, gone stale the moment the owner changed the MCP config or
a share was created after the upload. Sanitizing on read costs a string replace per page.

**Not covered:** credentials of library MCP servers (resolved at pod start, not on the record)
and the provider login the CLI keeps in its own files. The pod omits sign-in screens from uploads
(`sign-in-privacy.js`), but an agent that prints its own credential file shows it to every reader
of the history, the owner included; withholding those would need the hub to read the
credential secret on every page, which is the per-poll Kubernetes read this design avoids for
stopped sessions.
