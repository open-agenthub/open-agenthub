# Scheduled session editing proof (#243)

The schedule belongs to the CronJob, whereas agent runtime settings are baked into its pod
 template. A partial merge patch changes the schedule in place before storing it. Recreating
 the CronJob was rejected because it risks active Jobs and their history; changing only the
 database was rejected because the controller would keep using the old schedule. Runtime-field
 edits remain rejected by SessionUpdateValidator.

New and edited schedules use Etc/UTC explicitly. nextRunAt is the next nominal UTC occurrence,
 not a guarantee: Forbid concurrency and cluster availability can delay or skip a run. The shared
 parser validates before resources or records change. It supports five-field cron and calendar
 aliases; day-of-month and day-of-week use the controller's OR semantics. Invalid legacy rows
 return no estimate rather than failing a list request.

A database failure after the cluster patch can leave the stored schedule stale; retrying the
 update reconciles it. No distributed transaction spans the API server and the database.

## Run

Start the rootless API-server and Postgres as in the real-backend skill. Create the
`agenthub-sessions` namespace and `agenthub-agent` ServiceAccount. Configure the real backend
for that API server (unset in-cluster service environment variables and set its kubeconfig),
database and development authentication. Use a synthetic local API-key credential; no model
request is made. S3 and a kubelet are not needed for this path.

Run the backend from its `backend/` directory so it reads appsettings.json. Set
`DOTNET_GCHeapHardLimit=0x30000000`, disable build servers and shared compilation, and run
builds/tests and the browser proof sequentially on a 4Gi pod.

```
KUBECTL=<kubectl> PROOF_KUBECONFIG=<admin-config> node tests/SessionScheduleProof/proof.mjs > <proof.log>
CHROMIUM=<headless-shell> KUBECTL=<kubectl> PROOF_KUBECONFIG=<admin-config> node tests/SessionScheduleProof/browser.mjs <proof.log> <dialog.png>
```

The browser script resolves Playwright from browser-runtime and uses Vite on :5173 proxying
to the real backend on :8080. Supply unpacked native libraries/fonts through LD_LIBRARY_PATH
and FONTCONFIG_FILE when running rootless. The MCP script resolves its SDK from mcp/agenthub.
Use `PROOF_URL=http://127.0.0.1:8081 ... proof.mjs --baseline` against an unmodified main
backend to reproduce the schedule update silently ignored.

## Recorded result

- Unmodified main: token PATCH silently ignores schedule; existing CronJob remains unchanged.
- Branch: create/update validation, mode/runtime guard and token authentication pass.
- Token PATCH and real stdio MCP: existing CronJob UID retained, schedule changed, complete
  active Job and running Pod objects and job template unchanged, nextRunAt returned.
- Real Chromium: next run displayed, invalid schedule keeps dialog open, valid schedule saves
  and reopens with updated next run; CronJob identity and active Job preserved; no page errors.
- Backend suite with real Postgres: 2297 passed, zero skipped.
- Frontend suite: 855 passed; production build passes; token MCP: 51 passed; runtime suite: 747 passed.

The Job is created through the real API server from the actual CronJob template and its
status is marked active. No kubelet or agent container executes; the proof checks object
identity/spec/status/history preservation, not task execution or controller timing. Missing
transcript/state endpoints yield expected 404 responses in this no-runtime browser fixture.
