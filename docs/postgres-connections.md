# Postgres connections per replica

Every store used to build its own `NpgsqlDataSource`, and every data source is a separate pool
with Npgsql's defaults (up to 100 connections, idle ones closed after 5 minutes). Each store
opens a connection to create its schema, so one backend process held 29 idle connections right
after startup. A burst of 2000 API requests left 78–83 connections open. With the chart's two
replicas, a rolling update and a test run, that is more than `max_connections=100` on many
managed Postgres plans, and it caused the repeated `53300: sorry, too many clients already` in CI
(#366, #367, #373). This file records how connections are shared now (#377).

## What a replica holds

| Connection | Count | Lifetime |
|---|---|---|
| Shared pool (`application_name=open-agenthub`), used by every store | up to `Postgres:MaxPoolSize` | closed after 5 minutes idle |
| Lock pool (`open-agenthub-locks`), held advisory locks | up to `Postgres:LockPoolSize` (5) | opened on demand, closed after 5 minutes idle |
| Event bus `LISTEN` | 1 | process lifetime |
| Schema migration lock (#373), unpooled | 1 | startup only |

Measured with the real backend against Postgres 15 (`tests/PostgresConnectionsProof`):

| | after startup | peak under 2000 requests, 64 in flight | load time |
|---|---|---|---|
| before | 29 | 78–83 | 3.97 s |
| after, default (20) | 3 | 21–22 | 3.8–4.0 s |
| after, `Postgres:MaxPoolSize=5` | 3 | 6 | 3.98 s |

The ceiling per replica is `MaxPoolSize` (shared pool) + `LockPoolSize` (lock pool) + 1
(`LISTEN`) + 1 (migration at startup), or 27 with the defaults. For the whole install, multiply
by `backend.replicas`, plus one surge pod while a rolling update runs: 54 with two replicas, and
briefly 81. Many managed Postgres plans allow only
`max_connections=100`, with a few of them reserved for the provider. On such a plan, lower
`backend.database.maxPoolSize` or raise `max_connections` before you add a third replica. The pools
almost never reach their ceiling (about 20 at the load above), but the database has to allow for
it.

## Decisions

- **One pool per connection string, process-wide.** `PostgresDataSources.Shared(configuration)`
  keys the data source on the final connection string. The stores keep their `IConfiguration`
  constructors. The roughly 300 places in the tests that build a store by hand share one pool per test
  schema, as `new NpgsqlConnection(cs)` always did, so the suite's own connection count drops as
  well.
- **A second pool for advisory locks.** The browser, expiry, provider-login and role-change locks
  keep a connection for as long as the lock is held. A waiter in `pg_advisory_lock` also keeps
  its connection while it waits. In a single bounded pool, enough requests waiting for one
  session's browser lock would take every connection. The holder could then not get a
  connection for the queries it has to run before it releases, and everything would stall until
  Npgsql's 15-second acquire timeout failed it. Lock waiters can only exhaust their own pool.
- **The lock pool is small and sized on its own: `Postgres:LockPoolSize`, default 5.** Few locks
  are held at once. Only the browser lock blocks in `pg_advisory_lock`, and
  `KubernetesBrowserService` lets only one request per session and replica wait there, through
  an in-process semaphore. Every other user takes `pg_try_advisory_lock` and holds it briefly:
  a git or Claude token refresh, one session at a time in the expiry and cleanup sweeps, a
  runtime update, a role change. A request beyond the pool waits for a lock connection, up to the
  15-second `Timeout`, and then fails. Waiting there is what it would do at the advisory lock
  anyway, and the stores never feel it. Measured in
  `PostgresDataSourcesTests.LockPool_AtCapacity_StarvesNeitherQueriesNorQueuedLocks` at the
  defaults: 5 locks held and 4 more queued, during 1000 queries with 64 in flight. The queries
  took 289–296 ms, with p50 17 ms and p99 23–25 ms. The same burst with no locks held took
  289–297 ms, with p50 17 ms and p99 29–30 ms. The queued locks were granted 2–6 ms after
  release. Peak connections were 20 shared and 5 lock. A pool size or minimum in the connection
  string does not apply to this pool.
- **Default 20, not Npgsql's 100.** 100 per pool alone is the whole of `max_connections` on a
  small managed plan. 20 stayed at the same throughput as before in the load above. Npgsql queues
  a request that finds the pool exhausted, so a burst over the limit waits instead of failing.
- **Precedence: `Postgres:MaxPoolSize`, then a `Maximum Pool Size` in the connection string, then
  20.** The chart always sets the first. A plain `ConnectionStrings__Postgres` deployment that
  already sizes its pool keeps its size.
- **The migration lock stays unpooled.** A pooled connection would keep the session-level lock
  after `Dispose` until the pool hands the connection out again
  ([startup-schema-migration.md](startup-schema-migration.md)).
- **Nobody disposes the shared data source.** The event bus used to dispose its own. The shared
  one belongs to the process. Other hosted services still run queries while the host shuts down,
  so disposing it from one owner would fail theirs.

## Rejected

- **`Maximum Pool Size` alone.** It caps each of the 29 pools, not their sum, and the idle
  connection after startup stays open in each one.
- **`Pooling=false`.** This brings an idle process to one connection, as in the startup tests of
  #373. It would also make every query pay a TCP and authentication handshake.
- **A lock pool as large as the shared pool.** An earlier revision of this change did that, which
  allowed 42 connections per replica and briefly 126 for two replicas and a surge pod. That is
  over a small plan's 100, for connections that are almost never held at once.
- **Registering `NpgsqlDataSource` in the container and injecting it.** This is cleaner for
  production, but it changes about 30 constructors and about 300 hand-built stores in the tests, for the
  same pool. It also adds a second constructor with one parameter, which the container reports as
  ambiguous.
- **An external pooler (PgBouncer) in the chart.** It would add another component to operate, and
  transaction pooling breaks the session-level advisory locks and `LISTEN` that the backend relies
  on.
