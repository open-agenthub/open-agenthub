# Event bus shutdown proof

Run `bash tests/EventBusShutdownProof/run.sh` with the .NET 10 SDK on PATH and
`AGENTHUB_TEST_POSTGRES_CONNECTION_STRING` pointing to a disposable Postgres database.
The regression creates its own schema, starts the real backend DLL, waits for health,
sends SIGTERM, and requires exit code zero and no ObjectDisposedException in either output stream.
It runs alone in the startup-test collection to avoid exhausting database connections.

The event bus is tracked by the service container through several singleton registrations.
Disposal therefore must be idempotent: a second disposal otherwise calls StopAsync after
its cancellation source has been disposed. A single atomic guard preserves the first
stop-before-dispose sequence and lets genuine shutdown failures propagate. Catching
ObjectDisposedException was rejected because it would hide errors from the first shutdown;
changing registrations was rejected because ownership belongs to the disposable object,
and callers outside the host can also dispose it more than once.

For a negative control, save the fixed event-bus source outside the repository, replace only
that file with the main-branch version, run the script, and restore the saved source.
Do not commit logs or generated output.
