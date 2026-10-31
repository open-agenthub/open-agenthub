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

- **`lock_timeout` plus a retry per statement.** This would turn the race into retries scattered
  over 30 stores. It does nothing for `CREATE ... IF NOT EXISTS`, which fails on the unique index
  and not on a lock wait.
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
dependency cycle only exists in `Program.cs`'s container. `tests/StartupLockProof` is the
same check against a real 1.7.0 database, with `pg_locks` and `pg_stat_activity` captured while a
replica hangs.
