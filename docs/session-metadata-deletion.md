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
reads remain available during pruning, but constraint installation may briefly block reads
of the metadata table. Large installations should schedule the upgrade in a quiet period.
The referencing columns already have leading indexes (primary keys or the link session index),
so subsequent cascades use indexed lookups. A `NOT VALID` constraint followed by separate
validation was rejected here: it adds migration states and would not make the initial orphan
prune free of write coordination. Later startups skip pruning and validation.
