# Actual startup-store lock diagnosis

This diagnostic supports the separate startup issue #538. It does not change migrations.

```sh
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=54329;Username=postgres;Database=proof' \
dotnet run --project tests/ProviderRecoveryProof/StartupLockProbe
```

It creates/drops its own local database, initializes the real production session store,
holds a session row-lock transaction, then calls the same real initialization again.
Observe queued relation locking, a normal reader timing out behind DDL and the default
startup command timeout. Release the writer and verify initialization succeeds. The run
uses the actual production data-source and initialization code. No Kubernetes or native
CLI is involved, and it does not identify the actual lock holder of a failed worker run.
Keep generated logs outside Git. Allow about 30 seconds after compilation.
