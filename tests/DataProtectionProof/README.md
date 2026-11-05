# Shared Data Protection proof

Requires the .NET 10 SDK and a disposable Postgres database. It creates and drops a
unique schema; the backend uses a closed loopback endpoint as its Kubernetes stand-in.
No cluster, real credentials, or external API is used.

```sh
export AGENTHUB_TEST_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres'
dotnet run --project tests/DataProtectionProof
```

An optional first argument selects a built backend DLL. `DOTNET_HOST_PATH` can select
the dotnet executable used by the child processes. Certificates, homes and synthetic
secrets are generated per run and removed afterwards. Output belongs in the PR body
or session Files, never in Git.

The scenario tests two real production backend instances against one schema, HTTP
catalog and webhook reads, restart, fresh-HOME replacement, encrypted database keys,
no HOME ring, gateway/cleanup protectors with the production registration, unreadable
secret status and preservation/repair, wrong-certificate startup rejection and legacy
file-ring migration. The xUnit suite imports this same scenario.

Main baseline at 873fab6c: POST on A and GET on A returned 200; GET on B and a
fresh-HOME replacement A returned 500 (key not found). A restart retaining HOME may
retain framework file keys; empty HOME models a chart pod replacement. Baseline used
separate real processes and synthetic catalog secrets, with unchanged backend code.
