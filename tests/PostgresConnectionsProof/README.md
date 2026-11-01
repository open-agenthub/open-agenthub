# Postgres connections per replica (#377)

`measure.sh` builds the backend of a checkout, starts the real `AgentHub.Api.dll` against a new
database, and counts that database's rows in `pg_stat_activity`. It counts after startup, at the
peak of 2000 requests with 64 in flight over seven DB-backed endpoints, and after the burst.

```bash
./measure.sh <checkout> "Host=127.0.0.1;Port=54329;Username=postgres" [label]
Postgres__MaxPoolSize=5 ./measure.sh <checkout> "<connection string>" max5
```

It needs dotnet 10, `psql` and `curl` on PATH, and a Postgres user that can create databases. The
skill `rootless-postgres-backend-tests` shows how to run one without root. To compare, run it once
against a detached `origin/main` worktree and once against the branch. The results are in
`docs/postgres-connections.md`.
