# Startup with several replicas

The backend creates and migrates its Postgres schema itself, at every start, before it listens on
:8080. The chart runs two replicas, and on a fresh install or after an upgrade both start at the
same moment against one database. This file records how that startup stays safe and the
alternatives that were rejected (#371).

## What went wrong

The 1.8.0 release candidate never became healthy in the quickstart. Two separate faults were behind
it.

- **A dependency cycle deadlocked the host.** `TelegramPermissionNotifier` started depending on
  `ISessionService` so it could name the session. The session service already reaches every
  `IPermissionNotifier`: `KubernetesSessionService` → `INetworkSessionCleanup` → `NetworkPortService`
  → `IEnumerable<IPermissionNotifier>`. Two of those edges are factory registrations
  (`sp => sp.GetRequiredService<...>()`), and the container cannot see through a factory. So it
  did not report the cycle. It recursed until its stack guard moved the resolution to another
  thread, and that thread waited for the singleton lock the first thread held. There was no
  exception and no log line, and the process never listened. A single replica hung the same way.
  The notifier now reads the title from `ISessionStore`, which is below the session service and
  depends on nothing.
- **Two replicas raced the schema.** `CREATE TABLE IF NOT EXISTS` is not safe against itself: both
  replicas pass the existence check and the slower one fails on `pg_type_typname_nsp_index`. 1.7.0
  had this too. Kubernetes restarted the crashed replica and the second attempt found the tables,
  so the bug only cost a restart.

## One advisory lock around all of the schema work

`SchemaMigrationLock` takes one session-level advisory lock, on an unpooled connection of its own,
around every store's `InitializeAsync` and the MCP schema. One replica migrates at a time, and the
next one finds everything in place and only runs the idempotent statements.

- **Session-level, not `pg_advisory_xact_lock`.** Each store migrates on its own pooled connection
  and in its own transaction, so no single transaction spans the work. A transaction lock would
  need every store rewritten to share one connection.
- **Its own connection, unpooled.** On a pooled connection the lock would outlive `Dispose` until
  the pool hands the connection out again. A replica that dies while holding the lock loses the
  connection, and Postgres releases the lock with it.
- **Released in `finally`.** A failed migration does not leave the lock held for the next replica
  while this process is still shutting down.
- **Polled with `pg_try_advisory_lock`.** A replica that blocked in `pg_advisory_lock` would log
  nothing. A polling replica logs `Waiting for schema migration lock held by another instance` once,
  and how long it waited once it has the lock. After 10 minutes it gives up with an error that names
  the lock, so a stuck holder shows up as a failed start and not as a silent hang.
- **The chart's startup probe allows 10.5 minutes.** `/healthz` answers only after the migration, and
  the liveness probe alone would kill a waiting replica after about 45 seconds, whose restart would
  then wait again. The probe allows a little more than the lock's own timeout, so the clear error
  ends the wait and not the probe.

The migrations of #367/#368 take `LOCK TABLE sessions` and their own tables in that order. Under
the advisory lock they can never meet another replica's migration halfway. They can still wait for
an old replica's ordinary traffic during a rolling update, but those transactions are short and
take no advisory lock, so a wait cannot become a cycle.

## Rejected

- **`lock_timeout` plus a retry per statement instead of the advisory lock.** This would turn the
  race into retries scattered over 30 stores. It does nothing for `CREATE ... IF NOT EXISTS`, which
  fails on the unique index and not on a lock wait. (#538 does add a lock timeout, in one place and
  under the advisory lock, for a different problem; see below.)
- **A migration Job or init container.** That would be a second place that knows the schema and a
  second image entrypoint. The quickstart and plain `docker run` installs would need it too, while
  the lock keeps "the backend migrates itself" true everywhere.
- **Leaving the crash to Kubernetes.** This worked for 1.7.0 but costs a restart on every fresh install,
  and it hides real migration failures among expected ones.

## How it is tested

`StartupConcurrencyPostgresTests` starts the real `AgentHub.Api.dll` twice at once. It runs once
against a fresh schema and once against one turned back into the 1.7.0 shape: without the session
foreign keys and the deleted-session table, and with rows of deleted sessions. Both runs require
`/healthz` 200 from both processes within 60 seconds, and the upgrade run checks that the orphans
are gone and the constraints exist. Starting the real process is the point, because the
dependency cycle only exists in `Program.cs`'s container. `ProgramContainerResolveTests` runs
`Program`'s entry point up to `HostBuilt`, then resolves every singleton and hosted service with a
timeout, with the chat relays on and off. That catches the next cycle behind a factory without a
database. `ValidateOnBuild` cannot catch it, because it does not look inside factories. `tests/StartupLockProof` is the
same check against a real 1.7.0 database, with `pg_locks` and `pg_stat_activity` captured while a
replica hangs.

## An unchanged schema runs no DDL (#538)

"Only runs the idempotent statements" turned out not to be harmless. `ALTER TABLE ... ADD COLUMN
IF NOT EXISTS` takes ACCESS EXCLUSIVE before it finds the column already there. A transaction
holding a row lock on `sessions` (`SELECT ... FOR UPDATE`) made a starting backend's DDL queue
behind it; every read of `sessions` that arrived later queued behind the DDL, on the healthy
replicas too; and the new backend failed after the 30 s command timeout. Measured with the real
`PostgresSessionStore.InitializeAsync`: one plain `SELECT count(*) FROM sessions` waited 29.9 s.

`SchemaScript.ApplyAsync` now runs every store's script:

- **A hash of each applied script is recorded** in `schema_scripts` (store name, SHA-256 of the
  script text). When the recorded hash matches, the store runs `to_regclass` and one `SELECT`,
  nothing that takes more than ACCESS SHARE. A store whose script changed runs it again whole;
  the scripts stay idempotent, so that is safe, and nobody has to remember to bump a number.
- **A script that does run waits at most 2 s for a lock** (`SET LOCAL lock_timeout`), in one
  transaction with its record. On `55P03` the attempt rolls back and is retried after 0.5, 1, 2,
  4, then 5 s, for up to 3 minutes. A queued ACCESS EXCLUSIVE request blocks every read behind it,
  so its lock timeout is the bound on how long a migration stalls live traffic; the pause lets the
  queued reads through before the next try. Each retry logs the store and the open transactions
  (pid, application name, state, age), and the final error says the same, because the lock-timeout
  error itself does not name the holder.
- **The retries share the advisory lock's budget.** `SchemaMigrationLock` passes on where its
  10 minutes end, and no script retries past that. Without that, a replica that waited for the
  lock and then for a table could outlast the chart's startup probe (5 s × 126 = 630 s) and be
  killed without saying why. The liveness probe only starts after the startup probe has passed.
- **The advisory lock stays, and is polled.** It still keeps two replicas from racing the same
  script. It is taken with `pg_try_advisory_lock` once a second, never with a blocking
  `pg_advisory_lock`, so its wait is bounded and logged. Waiting for it holds no table lock, so
  the running replicas' reads are not affected by a replica that waits; the lock timeout only
  bounds waits on ordinary traffic.
- **Every data statement in the scripts is one-shot by construction**, so running them once per
  script version loses nothing. `sessions.agent_session_id` is backfilled and then made NOT NULL.
  The `session_usage` billing classification only matches rows with usage and no billing, and
  every current write sets billing with the usage. Every skill write sets `search_text`. The four
  orphan prunes only ran before their foreign key existed. Work that must run at every start stays
  outside the scripts: `SlackThreadStore.PruneDeletedAsync` and the startup work in `Program.cs`.
  None of the scripts contains a statement that cannot run in a transaction (`CONCURRENTLY`,
  `VACUUM`, `ALTER TYPE ... ADD VALUE`).

Limits, on purpose:

- The first start of this version on an existing database finds nothing recorded and runs every
  script once, with the bounded waits above. Every later start with the same scripts runs none.
- A backfill inside a script (the session-usage classification, the skills search text) runs when
  its script changes, not at every start. It used to re-run at each restart as a side effect.
- A schema changed by hand is not noticed. Deleting the store's row from `schema_scripts` makes the
  next start apply its script again.

Rejected for #538:

- **A longer command timeout.** The queued DDL would convoy reads for longer, not shorter.
- **Checking the catalogue per statement** (does the column exist, does the index exist) before
  running it. That is a parser for the DDL of 37 stores, and every new kind of statement would need
  a new rule; a hash of the text needs none.
- **Hand-maintained schema version numbers.** A forgotten bump silently skips a migration; the
  hash cannot be forgotten.
- **One hash or the release version for the whole schema.** Either would re-run all 37 scripts,
  and take all their locks, whenever anything changes — the release version even when nothing did.

`SchemaScriptPostgresTests` holds `SELECT id FROM sessions FOR UPDATE` against the real store:
on a current schema the start finishes at once and a read is not delayed; on an outdated one reads
are delayed by at most the lock timeout and the start completes once the lock is released; a lock
that is never released ends in the error that names the holder, with nothing applied.
`tests/SchemaLockProof` runs the same against main and this change, upgrades 1.7.0 and the previous
release with the real backends, and compares their catalogues with a fresh install.
