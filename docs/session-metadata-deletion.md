# Session metadata lifetime

Share grants, share links, MCP sharing policies and thread mappings reference `sessions(id)`
with `ON DELETE CASCADE`. The production `ISessionStore` registration is always
`PostgresSessionStore`; there is no alternative production registry. Store initialization
runs after the sessions schema exists, even when the enterprise license is inactive.

This makes the registry deletion the lifetime boundary for every caller: web DELETE,
remote token API, child deletion, MCP, expiry and account purge. A failed delete preserves
both the session and its metadata. A concurrent share or thread insert cannot recreate an
orphan after deletion. This deliberately replaces an application cleanup hook: hooks can
race inserts and can leave metadata behind after a partial failure. No chat bindings are
changed by this migration.

Each store initialization opens a transaction. Before installing a named foreign key, it
prunes rows whose session no longer exists. The constraint catalog makes this a one-off
prune, including after restart. A sessions table lock serializes replicas before the catalog
check; table locks hold concurrent writes until pruning and constraint installation commit.
A failed migration rolls back its prune and is retried at the next start.

The first upgrade scans these metadata tables and holds write-blocking locks until commit;
the parent lock allows reads, while metadata DDL locks can also block reads until commit. Large installations should schedule the upgrade in a quiet period.
The referencing columns already have leading indexes (primary keys or the link session index),
so subsequent cascades use indexed lookups. A `NOT VALID` constraint followed by separate
validation was rejected here: it adds migration states and would not make the initial orphan
prune free of write coordination. Later startups briefly take the same parent lock but skip pruning and validation.

Expiry notifications are sent after successful deletion. A notifier whose destination is
session metadata implements `ISessionExpiryNotifier`: the sweep captures an in-memory notifier
under its session lock before deletion, then invokes it afterward. The thread notifier snapshots
the channel, thread timestamp and transient status timestamp. It can remove the working status
and send the expiry message even though the mapping has already cascaded away. A failed delete
sends nothing and leaves the mapping available for retry. The snapshot is discarded after the
sweep attempt; there is no retained destination table. A backend crash between deletion and
notification can still lose the notice, as with the existing post-delete delivery semantics.

Sending before deletion was rejected because storage or cluster cleanup can fail afterward;
a notice must not claim that a session was deleted while it still exists. Looking up the thread
only after deletion was rejected because the mapping is then correctly gone.
