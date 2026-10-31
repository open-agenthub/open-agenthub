# Session metadata deletion proof

The linked test sources run in the backend test project with real Postgres. Generated output
belongs outside Git.

```sh
export AGENTHUB_TEST_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres'
dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~SessionMetadataDeletion
```

The migration proof starts from unconstrained legacy tables containing both live rows and
orphans. It runs the production initializers twice, checks preservation and pruning, then
checks cascades. A rolled-back delete preserves metadata; writes creating new orphans fail.
