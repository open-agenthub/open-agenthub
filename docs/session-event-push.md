# Pushing session changes instead of polling for them

## What was wrong

The workspace polled. `SessionWorkspace.vue` asked for the file presentation every 1.5 s and,
whenever the Files pane was open, re-read the whole file listing with it. Two further loops ran
alongside: pending permissions and agent messages every 4 s, and the session list every 5 s.

That cost scales with **open browser tabs**, not with sessions — a session nobody is watching
costs nothing. Two places broke that rule and are worth naming, because both were found while
measuring rather than guessing:

- `KubernetesSessionService.ListSessionsAsync` called the ephemeral-MCP store once per session
  the caller owns. An owner of three hundred sessions therefore produced three hundred queries
  every five seconds from a single tab. Fixed by `ListSessionsWithEntriesAsync`, one batched
  query, with the per-session walk kept as the interface's default so in-memory stores stay
  correct unchanged.
- The same method listed pods from the Kubernetes API server on every request. etcd cannot index
  labels, so the apiserver fetches every pod in the namespace and applies the selector afterwards
  — the call scales with the namespace's total pod count, not with the caller's sessions, and
  without a `resourceVersion` it is a quorum read against etcd rather than a watch-cache read.
  Passing `resourceVersion: "0"` moves it to the cache. The staleness is milliseconds and the
  result was already advisory: a missing pod falls back to the stored status.

Replacing the LIST outright with a shared informer would remove it entirely and would deliver
phase changes as events. That is the right end state and is deliberately not done here — the
reconnect, resync and bookmark handling it needs exists nowhere in this codebase yet, and it is
worth its own change rather than riding along with this one.

## Why Postgres LISTEN/NOTIFY, and not SignalR

The backend runs with more than one replica (`values.yaml`: `replicas: 2`). The write that
changes a session and the socket that has to hear about it routinely land on different pods, so
an in-process event bus delivers to the wrong one about half the time.

SignalR was the obvious candidate and does not solve this. Its groups only span replicas with a
backplane, and the official backplane is Redis — a component this project does not run. Postgres
LISTEN/NOTIFY would still be needed underneath it, which would make SignalR a layer over the
solution rather than the solution. What SignalR genuinely offers is reconnect-with-backoff and
transport fallback; the fallback buys nothing here because the terminal already requires
WebSockets, and the backoff is twenty lines that `BrowserPane.vue` had already written.

NOTIFY is also delivered on commit, which gives two properties for free: a rolled-back write
produces no event, and identical `(channel, payload)` notifications raised in one transaction are
collapsed, so marking a whole session's files deleted wakes a client once rather than once per
row.

## Why a database trigger rather than a publish call

`session_files` and `session_file_presentations` are written from four files — the upload
controller, the agent callback controller, the preview worker and the expiry sweep — across
roughly eight statements. A write path that forgets to publish fails nothing: the suite stays
green, and the only symptom is one user's pane staying stale until they reload. That is the kind
of defect that survives review.

An `AFTER INSERT OR UPDATE OR DELETE` trigger on both tables cannot be bypassed by a new write
path, including one added in `ee/`. It lives in `PostgresSessionFileRegistry.InitializeAsync`
next to the DDL it belongs to.

## Why the socket carries no data

A message is `{"type":"files"}` and nothing more. The client answers by re-reading the REST
endpoint it was previously polling, and that endpoint already decides what a shared-link viewer
is allowed to see. Shipping the changed state over the socket would create a second place that
has to make the same authorization decision, and the two would drift.

The same reasoning sets the re-authorization interval. `BrowserProxy` re-checks every two seconds
because it relays live screen content; this socket carries none, so a viewer whose access was
revoked learns only that *an* event occurred, and the read they attempt next is refused. Thirty
seconds is the trade.

## The fallback poll stays

While the socket is connected the poll runs every 30 s instead of every 1.5 s; when the socket
drops it returns to the configured cadence immediately, rather than waiting out the slow
interval. It exists for the case the push cannot cover — a replica dying between the commit and
the notification reaching a subscriber. Removing it would turn a rare lost event into a pane that
never recovers.

## A trap worth remembering

The first version resynchronised every subscriber whenever the listener connected, including the
*first* connect. That emitted an event indistinguishable from a real one, and the tests passed
with the trigger deleted — a broken trigger would have looked healthy in production until the
second change to a file. `StartAsync` now waits until `LISTEN` is actually established, and the
resync only runs from the second connect onwards, where a gap genuinely exists.

The corresponding trap on the frontend: a Vue test that mounts a component without unmounting it
leaves its poll timer running into the next test, and cadence assertions then count two
workspaces at once.
