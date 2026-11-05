# Who may drive a session from its Slack thread

A session's Slack thread is not always a DM. With a channel override, the instance fallback
channel (`Ee:Slack:Channel`) or `!new` typed in a channel, the thread lives where every member of
the channel can post in it and press its buttons. Since #436 the hub accepts thread replies,
`!status` and permission-button clicks only from the session's owner, or a Collaborator of a
shared session. Everyone else is refused without their input reaching the session.

## Identity: the same email mapping as `!new`

Slack tells the hub a user id (`U…`) and nothing about AgentHub accounts. `SlackSenderGate`
asks `users.info` for the user's workspace-verified email and accepts it only when it matches
exactly one AgentHub account (`UserDirectory.GetUniqueByEmailAsync`) — the mapping `!new` and
`!repos` already trust, and the one the notifier uses the other way round (email → DM). Two
accounts with the same address make the sender unknown rather than a guess.

Rejected: a separate linking step with a one-time code, as Telegram and Signal have. Slack
already verifies the email for the workspace, and every Slack user of the instance would have
to link before their own DM threads kept working. Also rejected: treating "posted in a thread
whose channel is the owner's DM" as proof. It covers DMs only, and a channel thread is exactly
the case that needs a check.

`SlackEnabled` is not required here, unlike for `!new`: it records consent to be notified, not
who someone is. Requiring it would lock owners out of the threads they already have once they
switch notifications off.

## Access: SessionAccessRules, owner decided first

Once mapped, the account needs write access: `SessionAccessRules.CanWriteTerminal`, i.e. owner
or Collaborator, the same rule as the shared terminal. The owner is compared directly with the
thread's stored owner before any share lookup, so the owner's own replies never depend on the
share store or on the licence state of sharing (#421 was a same-owner regression of exactly this
shape). A Collaborator is resolved through `ISessionAccessService`, which answers nothing for
non-owners without an enterprise licence, and the resolved session's owner must match the
thread's.

`!status` is gated too: it shows the pending tool, which is the owner's information.

Button clicks carry only the permission request id. The hub reads the request (session, owner)
and checks the clicker before resolving it, and before the "Already decided" rewrite: a refused
click changes nothing about the owner's prompt.

## Refusing

A refused sender gets one `chat.postEphemeral` notice — visible to them only — per session (or,
for an unknown Slack user, once in total, with a hint how the mapping works). It never quotes
what they sent. Rejected: a normal thread reply, which would show the whole channel who was
turned away and repeat on every message; and silence, which leaves a colleague wondering why the
agent ignores them. "Once" is kept in memory: after a restart, or on another replica's socket, a
sender may be told once more, which costs nothing.

## Not covered

Reading is not restricted: everyone in the channel sees the thread, because Slack shows it to
them. The settings dialog says so when a channel override names a channel rather than a DM.
