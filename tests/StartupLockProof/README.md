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
