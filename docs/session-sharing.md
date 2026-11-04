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
3. Among equally strong grants, the most specific is reported as the reason (`inheritedFrom`):
   a grant on the session itself before an inherited one, a nearer ancestor before a farther one.
   This changes the label, never the level.

Grants that reach a session today:

| Scope | Source | Reaches |
|---|---|---|
| `Session` | direct share on the session | that session |
| `Session` | link on the session (token holder only) | that session, and nothing else |
| `Ancestor` | direct share on a session up the `parentSessionId` chain | every descendant |

**The alternative to "strongest wins" was "nearest wins"**: a child shared as Viewer under a parent
shared as Collaborator would open as Viewer. It was rejected because it makes a grant a deny. To
keep a collaborator out of one child, the owner would add a share that takes access away, and
revoking that share would silently give it back. With "strongest wins" every grant is additive,
and the only way to take access away is to remove a grant — which is what the share dialog
already shows.

Project-wide sharing (#409) plugs in as one more scope: the store adds the project's grants for
the principal to the list it hands to `Decide`, and the precedence above stays as it is.

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

`GET /api/sessions/shared` returns every session a direct share opens for the caller, including
inherited children, in the sanitised shape of `SharedSessionInfo` with `accessRole`, `sharedBy`,
`inheritedFrom` and — only when the caller can open the parent too — `parentSessionId`. The web
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
