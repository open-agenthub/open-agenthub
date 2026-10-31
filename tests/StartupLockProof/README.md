# Backend startup with several replicas — proof (#371)

`run.sh` starts N copies of the real backend (`AgentHub.Api.dll`) at the same moment against one
Postgres database, with the quickstart's configuration (API key auth, no OIDC), and reports when
each answers `/healthz` 200. If one is still not healthy after `STALL` seconds it dumps
`pg_stat_activity` (with `pg_blocking_pids`) and `pg_locks`.

`upgrade.sh` prepares a 1.7.0-shaped database: the 1.7.0 backend creates its schema, then rows
whose session no longer exists are added to the tables that #367/#368 give a foreign key.

Setup: Postgres from skill `rootless-postgres-backend-tests` (port 54329, `PG_BIN` pointing at
its `bin`), the .NET 10 SDK, and a kubeconfig at `~/.kube/config` whose server is unreachable
(`http://127.0.0.1:1`): the backend needs one to start and never needs to reach it for `/healthz`.

```bash
createdb -h 127.0.0.1 -p 54329 -U postgres fresh
OUT=out/fresh ./run.sh <branch>/backend/bin/Debug/net10.0/AgentHub.Api.dll fresh 2 60
./upgrade.sh <v1.7.0>/backend/bin/Debug/net10.0/AgentHub.Api.dll upgraded
OUT=out/upgraded ./run.sh <branch dll> upgraded 2 60
```

When a backend hangs without a waiting lock, take its managed stacks with
`dotnet-stack report -p <pid>` (`dotnet tool install -g dotnet-stack`); that is how #371 turned
out to be a dependency-injection deadlock and not a database lock.

## Result (2026-10-31)

| Backend | Database | Replicas | Healthy |
|---|---|---|---|
| main 58ee691 | fresh | 1 | never (60 s); every connection idle, no lock waiters; managed stacks show the DI deadlock |
| main 58ee691 | fresh / 1.7.0-shaped | 2 | 0/2 within 60 s |
| v1.7.0 d7363d3 | fresh | 1 | 1.5 s |
| v1.7.0 d7363d3 | fresh | 2 | 1/2 in 3 of 3 runs: the other crashes on `pg_type_typname_nsp_index` |
| branch | fresh | 1 | 1.5 s |
| branch | fresh | 2 | 2/2 in 3 of 3 runs, at most 2.6 s |
| branch | 1.7.0-shaped | 2 | 2/2 in 3 of 3 runs, at most 2.2 s; all 4 foreign keys added, orphans removed |
| branch | fresh | 3 | 3/3, at most 3.2 s |
| branch | fresh, lock held 8 s by `psql` | 1 | logs the wait, acquires after 7.0 s, healthy at 8.6 s |
