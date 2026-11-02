# A session chosen with /use stays chosen

A Telegram or Signal chat has one active session: plain messages, with no topic and no reply,
are typed into it. Until #398 every question a session posted made that session the active
one (`SetActiveAsync` in both notifiers). That is right for a chat that never chose a session,
because the user answers whatever just spoke. It also overwrote an explicit `/use` (`!use`)
choice. The owner chose a session, another session asked something, and their next plain
message was typed into the other session's terminal.

## The pin

`/use` now records the choice in `chat_pins` (one row per platform and chat: session, owner).
While a pin exists:

- notifications from every session are still posted, with their names, but the notifier's
  `FollowAsync` does not move the active session;
- a plain message goes to the pinned session;
- a message in a session's forum topic, or a reply to one of its messages, still goes to that
  session. The topic or the reply says where it is meant to go; the pin covers only messages
  that say nothing.

Without a pin nothing changes: the last session to post is active.

`/use` of another session moves the pin. `/unpin`, or `/use auto`, removes it, and the next
session to post becomes active. `/new` moves an existing pin to the session it started, because
the user just asked for that session; it does not create a pin where there was none. The
command menu, the `/use` usage hint and the `/use` confirmation all name `/unpin`.

## A table, not a flag on the binding

A pin could have been a second boolean beside `active` in `chat_session_bindings`. It was not,
for two reasons. The delete removes the session's binding (#365), and the pin has to outlive it:
the pinned session's deletion is exactly the case where the chat must not fall back to another
session. A flag would also be one more value the notifier has to leave alone in a row it
rewrites, while a separate row is one `NOT EXISTS` in the notifier's update. That update, `/use`
and `/new` take a transaction-scoped advisory lock per chat first. Without it, a `/use` that
commits while a notification's update waits for the same binding rows is overwritten, because
the update re-checks the rows but not its own `NOT EXISTS`.

## When the pinned session is gone

The delete leaves the pin in place and removes the binding, so nothing is active. A plain message
(or `/status`) then gets `🗑 The session you selected with /use, <name> (#ab12), was deleted.
/sessions lists the remaining ones, /use <tag> picks one, /unpin follows the newest session
again.` The name comes from the #365 deleted-session marker. Once the marker has been pruned
after 30 days, only the tag is shown. Other sessions that post meanwhile do not take over,
because the pin still exists.

The rejected alternative was to clear the pin on delete and fall back to following the latest
session. That is the original failure in a different form: the message the user meant for the
deleted session would go to whichever session spoke last, and nothing would tell them.

## Ownership

A pin records its owner. When a chat is linked to another account, the old account's pin would
otherwise keep the new owner's chat on a session they cannot reach, with no notification able to
change it. `FollowAsync` therefore first drops a pin whose owner differs from the posting
session's owner. The deleted-pin answer is given only to the account the pin belongs to. The
account purge deletes the account's pins, including pins on the account's sessions.

## Proof

`tests/TelegramSessionNamesProof/PinnedSessionScenario.cs` (`dotnet run -- --pinned-session`,
also `TelegramSessionPathsTests.UseKeepsTheChosenSessionWhileOthersPost`) runs the production
router, notifier and binding store on Postgres. The Bot API is a recorder, each session has its
own stand-in pod on a separate loopback address, which the production `AgentTerminal` types into,
and A is deleted through `KubernetesSessionService.DeleteSessionAsync`. It runs in a private chat
and in a forum supergroup. At `680f9e1` (`record-pinned-baseline.sh`), after `/use A` the plain
message reached the session that posted last, and after A's deletion it reached B. With the pin it
reaches A, and after the deletion it gets the error. `ChatSessionDeletionPostgresTests` covers
Signal, `/new`'s pin move and a pin left by another owner. `AccountPurgePostgresTests` covers the
purge.
