# What a chat keeps of a deleted session

A Telegram or Signal chat knows a session through its binding (`chat_session_bindings`: chat,
forum topic, the chat's active marker) and its reply-routing rows (`chat_messages`). Until #365
nothing removed either. Every delete — web UI, REST, remote and in-pod MCP `session_delete`,
child cleanup, auto-delete, account deletion — left them in place, so `/sessions` printed each
deleted session as `#e8d5 · Unknown` (about 95 lines on one install, mostly a coordinator's
deleted children), and `/use` could make a deleted session the active one again.

## Where the cleanup runs

All delete paths end in `KubernetesSessionService.DeleteSessionCoreAsync`; the expiry sweep
calls it directly because it already holds the session lock. The chat cleanup is a step there,
before the session row is removed, rather than a call in each caller: a seventh delete path
added later gets it without remembering to. Like the other steps it is idempotent, so a delete
that failed further on and is retried runs it again.

The alternative, a foreign key from the bindings to `sessions` with `ON DELETE CASCADE`, was
rejected: the binding store does not own that table, and a cascade would also take the
information the next section needs.

## A marker, not just a delete

Removing the binding outright loses two things:

- **The expiry notice.** The sweep deletes the session and then notifies; the notifier found
  the thread through the binding, which no longer exists at that point.
- **An answer for the chat.** With the binding gone, the next plain message in the chat (or in
  the session's forum topic) found no session and got "no active session", which hides that the
  session the user was talking to was deleted.

So the delete moves each binding into `chat_deleted_sessions` (chat, topic, title, whether it
was the active one). The notifier falls back to it for `finished`/`failed`/`session-expired`,
under the same "is this still the owner's linked chat" check as a live binding, and records no
reply-routing row for it. The router answers a message in the topic, or a plain message while
the deleted session was the active one, with `🗑 … was deleted. /sessions lists the remaining
ones, /use <tag> picks one.` Choosing another session (`/use`, `/new`, a new question) clears
the active flag on the markers, so the answer stops once it would contradict the new choice.
A reply to one of the deleted session's old messages gets the generic answer: its
reply-routing rows are gone, and keeping them only to recognise that case was not worth a
second kind of retained row.

Markers are pruned after 30 days, like reply-routing rows, and the account purge removes them.

## Existing installs

Bindings left by deletes before this change are removed by the poll services' prune, which
now also runs once at their start instead of only after the first 24 hours. It turns them into
markers too, without a title, because on an upgraded install the chat's active session is
typically one of them. The prune also drops reply-routing rows whose session is gone.

## /sessions

`/sessions` (and Signal's `!sessions`) lists only sessions the session service still returns,
from one `ListSessionsAsync` call instead of a lookup per binding, most recently used first, the
active one marked. It shows at most 15 lines and says how many older ones exist; the active
session is always among the lines even when it is older than the fifteenth. `/use` refuses a
tag whose session no longer exists, so a binding the prune has not reached yet cannot become
the active one.

## Forum topics stay as they are

A deleted session's Telegram forum topic is neither closed, renamed nor deleted. Deleting would
destroy the user's history. Closing or renaming needs a Bot API call from the delete path. That
couples every delete, including the expiry sweep and account deletion, to Telegram being
reachable, and the bot keeping `can_manage_topics`, or it fails silently. A closed topic also
stops the user from writing into it, which is how they would learn what happened. The topic
therefore stays open, keeps its name, and a message in it is answered with the marker.

## Proof

`tests/TelegramSessionNamesProof` (`dotnet run -- --deleted-sessions`, and
`TelegramSessionPathsTests.DeletedSessionsLeaveTheChat`) runs the production router, notifier,
binding and session stores on Postgres, the real `KubernetesSessionService.DeleteSessionAsync`
and the real expiry sweep, with loopback stand-ins for the Bot API and the Kubernetes API.
`record-deleted-baseline.sh` runs the same steps against the revision before the fix.
`ChatSessionDeletionPostgresTests` covers the store, the prune of existing stale rows, Signal and
the account purge.
