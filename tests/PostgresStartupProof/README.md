# Bundled database startup proof (#416)

Dispatch `install-smoke.yml` on the issue branch with `prove_postgres_startup=true`.
The unmodified quickstart creates real k3s and uses this checkout's backend images.
No host profile, sysctl, runtime or confinement policy is modified by the proof.
A manually forced multi-user SQL CHECKPOINT still fails on affected hosts; it is
not a success assertion for a workaround that avoids that signal requirement.

`prove.sh` checks that both installed database containers have zero restarts and
initialization exited successfully, then starts five additional fresh releases
from the actual chart template with `postgres:16-alpine`, UID 999, fsGroup 999 and
non-root execution. It checks zero restarts, password authentication via pod IP (including a rejected
incorrect password), and the single-user backend's real shutdown checkpoint. Logs retain the single-user shutdown checkpoint and the main server's
clean start. Run five consecutive successful workflow dispatches on the final
branch head; record run links and image identities in the PR body.

`baseline.sh` preserves the original chart's single-container first-start behavior
for diagnosis on a real k3s host. It prints termination state, previous entrypoint
output, task confinement labels, runtime-directory ownership and kernel audit
messages before deleting each namespace. A successful baseline does not disprove
the intermittent defect; check signal audit records too. The PR baseline workflow
reproduced exit code 1: pg_ctl failed to signal its temporary server, followed by
the same checkpoint signal denial in recovery. This was not an OOM kill.

`DatabaseStartupRetryTests` exercises the real database wire protocol for 57P03 and
invalid credentials, actual refused sockets, cancellation and deadline expiry.
With `AGENTHUB_TEST_POSTGRES_CONNECTION_STRING` set, its integration case first
refuses TCP connections, returns two protocol recovery errors, then forwards the
same startup path to a real database. The actual schema advisory lock is acquired
and released, and the migration runs once.

Local commands:

```sh
bash -n helm/open-agenthub/files/init-postgres.sh
bash -n tests/PostgresStartupProof/prove.sh
helm lint helm/open-agenthub --set postgres.password=proof
pwsh tests/helm/database-values.ps1
dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~DatabaseStartupRetryTests
```

Generated output belongs in workflow logs or session Files, never in Git.
The local helper-container check uses UID 1000 because the session helper API
fixes the UID; only the k3s proof claims the chart's exact UID/security context.
The init fix avoids first-start pg_ctl signaling, not every signal a running
server needs. Host policy repair remains necessary for broken subsequent recovery;
the chart's unconfined option is an explicit, false-by-default last resort.
