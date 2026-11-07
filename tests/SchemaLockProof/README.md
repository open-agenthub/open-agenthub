# Startup DDL behind a held session lock — proof (#538)

Setup: Postgres and .NET as in `../StartupLockProof/README.md` (port 54329, `PG_BIN`, a kubeconfig
whose server is `http://127.0.0.1:1`). Output goes to `OUT`; keep it out of Git.

## Probe — the real store initialisation

`Probe/` calls the real `PostgresSessionStore.InitializeAsync` with the production data source
while another transaction holds `SELECT id FROM sessions FOR UPDATE` for `HOLD` seconds (10), and
reads `SELECT count(*) FROM sessions` every 100 ms on its own connection. Scenarios: `current`,
`outdated` (a column dropped, the store's record marked older) and `unrecorded` (current, but no
`schema_scripts`: a database left by a release before #538). `-p:Backend=<checkout>/backend`
compiles it against another checkout for the before/after.

```bash
export AGENTHUB_TEST_POSTGRES_CONNECTION_STRING="Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres"
dotnet build Probe -o $OUT/branch && dotnet build Probe -p:Backend=<main>/backend -o $OUT/main
dotnet $OUT/branch/Probe.dll current      # also: outdated, unrecorded
```

## Real backends

- `upgrade.sh <branch dll> <v1.7.0 dll> <previous release dll>`: a fresh database, one created
  by 1.7.0 and one by the previous release, each started with two branch replicas at once, then
  restarted; compares `catalogue.sql` (columns, indexes, constraints, triggers, functions, sorted)
  of each upgraded database with the fresh one.
- `held-start.sh <dll> <database> [hold] [timeout]`: starts one backend while `psql` holds the row
  lock, and reads `sessions` every 0.2 s.

## Result (2026-11-07, main 5be2341 = 1.21.1)

Probe, writer released after 10 s:

| Code | Scenario | InitializeAsync | Slowest read | Reads over 1 s |
|---|---|---|---|---|
| main | current | fails after 30.0 s (command timeout) | 29 979 ms | 1 (the only read) |
| main | outdated / unrecorded | fails after 30.0 s | 29 990 ms | 1 |
| branch | current | ok in 0.01 s, no queued lock seen | 1 ms | 0 of 100 |
| branch | outdated | ok at 15.5 s, after the release | 1 998 ms | 4 of 76 |
| branch | unrecorded | ok at 15.6 s, after the release | 1 997 ms | 4 of 77 |

Real backend, `held-start.sh` on a database created by 1.21.1 with one session:

| Backend | Lock held | Result | Slowest read |
|---|---|---|---|
| 1.21.1 restart | 45 s | exits: `NpgsqlException` / `TimeoutException` | 29 905 ms |
| branch, first start | 20 s | 5 logged retries naming `pid … (psql, active, …)`, healthy at 23.9 s | 1 993 ms |
| branch, restart | 45 s | healthy at 1.0 s | 10 ms |

`upgrade.sh`: fresh, 1.7.0 and 1.21.1 databases all became healthy with two branch replicas
(≤ 2.2 s) and on restart (1.0 s); each recorded 35 scripts (the two MCP OAuth stores run only with
`Mcp:PublicUrl`), and both upgraded catalogues are identical to the fresh one (622 lines).
