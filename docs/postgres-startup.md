# Bundled database startup

Issue #416 was a signal-policy failure, not an out-of-memory restart or a socket
permission failure. A real install with `postgres:16-alpine`, UID 999 and fsGroup
999 exited with code 1 immediately after `CREATE DATABASE`: the image's `pg_ctl`
could not send SIGINT to its temporary initialization server. The host audit log
identified the default confinement profile denying a signal to its own stacked
`//&unconfined` peer. Recovery then failed to signal its checkpointer for the same
reason. A checkpoint eventually ran on its timer; that did not repair the denied
signals or make the recovering server usable.

The chart initializes a fresh volume in an init container. It reuses the image's
initdb password and host-authentication helpers, then creates the application
database with `postgres --single`. Single-user mode writes its own checkpoint and
exits without signaling a temporary server. The main entrypoint sees an existing
cluster and starts normally. Existing initialized volumes are left alone. A
pending marker retries database creation if initialization is interrupted; an
already-created application database is accepted. Initialization errors are logged
and block startup rather than letting the main container expose a partial setup.
The script depends on the bundled image's shell helpers; alternate images must
provide that contract. Image updates must run the real startup proof.

A writable socket directory cannot fix a denied `kill(2)` call: the image's
`chmod /var/run/postgresql` warning was also present in healthy starts. Resource
requests cannot repair signal policy either. Readiness and startup probes use TCP
`pg_isready`, so a socket-only temporary server cannot make the pod ready. There is
no liveness probe to repeatedly interrupt recovery. We retain UID 999 to keep
existing volume ownership compatible instead of changing to the image's UID.

Backend startup retries only recovery/startup SQLSTATE 57P03 and refused TCP
connections, with exponential delays from 250 ms to 5 seconds. Connection waiting
and advisory-lock contention share the existing ten-minute budget, below the
630-second backend startup probe. Invalid credentials fail immediately. Migration
bodies are never replayed: retrying arbitrary schema changes after a partial
failure could repeat destructive work. Logs say `waiting for database to accept
connections` while retrying.

The init change avoids the first-start signal failure; it does not repair the
host policy for future crashes, shutdowns or recovery. On an affected host, manual
`CHECKPOINT`, `pg_basebackup` and other backups that request a checkpoint can fail,
and recovery after a database crash can stall on the same denied signal. For
production, use `externalDatabase.connectionString` with `postgres.enabled=false`
instead of relying on this bundled development database. Prefer updating the host
runtime/profile to allow signals between its own stacked peers. As a last resort,
`postgres.appArmorUnconfined=true` disables AppArmor for the bundled database's
init and server containers only. It defaults to false. This removes that layer of
filesystem and signal confinement for those containers, while keeping their
non-root identity; use an external database instead when that tradeoff is not
acceptable. We rejected disabling confinement by default or modifying host policy
from the chart or install-smoke workflow. A host-modified gate would no longer
prove what a normal installation experiences.

See `tests/PostgresStartupProof/` for reproduction commands and proof scope.
