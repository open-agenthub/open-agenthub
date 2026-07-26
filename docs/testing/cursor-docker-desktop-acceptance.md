# Cursor Docker Desktop acceptance

Date: 2026-07-25 (matrix and local gates); follow-up clarification and Claude/Codex
smoke on 2026-07-26
Base commit: `5e768e9c83137e7e1ddb22e09862474b5ea38749`
Kubernetes context: `docker-desktop`

## Result

Cursor support passed the full local regression suite, a five-image Docker Desktop
deployment via `setup-dev.ps1 -NoPortForward`, and an isolated API/Kubernetes matrix that
used only synthetic fixtures. That live matrix covers Cursor mode/auth selection,
selected-only credential projection, missing-credential fail-closed, unauthenticated
Interactive login reachability, edit/duplicate, and Scheduled CronJob shape. It does
**not** claim live Docker Desktop coverage for subscription Secret upload, policy
allow/deny behavior, MCP fail-closed in-pod, pause/resume with `cursor-state.tgz`, or
terminal reconnect/scrollback; those remain unit/component evidence (see mapping and
Limitations).

No real Cursor account data, host credential storage, browser storage, token, or
Kubernetes Secret value was copied into the cluster or into this report. Real
account-bound Cursor subscription login remains a user action inside the session
terminal and was never completed.

## Environment and local gates

| Tool | Version |
| --- | --- |
| .NET SDK | 10.0.301 |
| Node.js | 24.16.0 |
| npm | 11.13.0 |
| Helm | 3.19.1 |
| kubectl client | 1.34.1 |
| Docker client/server | 28.5.2 / 28.5.2 |
| Cursor Agent CLI (image) | `2026.07.23-e383d2b` |

Local verification recorded on this branch:

- Backend: 331 passed, 0 failed, 37 skipped PostgreSQL/integration tests when no test
  connection string was supplied.
- Frontend: 21 files and 172 tests passed; production build transformed 81 modules.
- Session runtime: 108 passed, 0 failed (includes Cursor driver, auth watcher, MCP, and
  `cli-config` coverage).
- Helm: `helm lint` reported 1 chart and 0 failures; `tests/helm/cursor-runtime-values.ps1`
  passed (default, dev, fallback, override).
- `git diff --check` passed.

## Images and deployment

`kubectl config current-context` was exactly `docker-desktop`. The supported
`.\setup-dev.ps1 -NoPortForward` path built and deployed all five images. Release
`agenthub-dev` rolled out successfully; control-plane Pods remained Running/Ready.

| Tag | Local image ID |
| --- | --- |
| `open-agenthub-dev/backend:local` | `sha256:01ade6819be53d4b27facbb34e4aff503ffebd077db5f244002f39c677ff15df` |
| `open-agenthub-dev/frontend:local` | `sha256:3020b482725232a43626a0c543a95c8340c2e93cd08362c7e38034bee6949e2a` |
| `open-agenthub-dev/agent-runtime-claude:local` | `sha256:81f4647d7ca6e7fd639a49bb08fe03498450a44582dadb82693bc607a692555c` |
| `open-agenthub-dev/agent-runtime-codex:local` | `sha256:ac694b5f32c8c6a266dc8b8e2191ec73aa8df69f5d9db92f46eac96e55ad48f1` |
| `open-agenthub-dev/agent-runtime-cursor:local` | `sha256:9a571db33a2b2b2399c0dcd429db50d6385acf8257514a9cfeb39da3898e1105` |

ConfigMap keys included:

- `AgentHub__ClaudeAgentImage: open-agenthub-dev/agent-runtime-claude:local`
- `AgentHub__CodexAgentImage: open-agenthub-dev/agent-runtime-codex:local`
- `AgentHub__CursorAgentImage: open-agenthub-dev/agent-runtime-cursor:local`

## Acceptance matrix

| Evidence surface | Case | Outcome |
| --- | --- | --- |
| Kubernetes + API | Cursor Interactive / Subscription without stored auth | Session `6b634964800c` reached Running/Ready on `agent-runtime-cursor:local`. Pod auth mode was `subscription`, mounted volume `cursor` only (no Claude/Codex volumes), no `CURSOR_API_KEY`. Entrypoint started `bash .../cursor/login.sh` under the shared PTY. Real login was not completed. |
| Kubernetes + API | Missing Cursor Subscription for Autonomous | Session `a4d1ab064f21` recorded `phase=Failed`. No Pod was created in `agenthub-dev-sessions`. |
| Kubernetes + API | Missing Cursor API key for Scheduled | Session `98d8f5540aab` recorded `phase=Failed`. No CronJob or Pod was created for that session. |
| Kubernetes + API | Cursor Autonomous / API key with synthetic key | Session `eb49afeafc58` started on the Cursor image with `CURSOR_API_KEY` referenced from `cursor_api_key` only; no `cursor` subscription volume; no Claude/Codex credential projection. Pod terminated Failed after synthetic provider rejection. Not claimed as real-model execution. |
| Kubernetes + API | Cursor Scheduled / API key | CronJob `session-cfdc54cf470f` used schedule `0 0 31 2 *`, `Forbid`, `Never` restart, Cursor image, and selected-only `CURSOR_API_KEY`. No Job was manually triggered. |
| Kubernetes/API | Edit and duplicate | PATCH on `6b634964800c` round-tripped Cursor + ApiKey + policy. Duplicate `3f6df91198f5` preserved Cursor/ApiKey with an explicit empty default-deny policy. Live Pod for the patched session retained the original Subscription projection until restart (expected). |
| Kubernetes | Selected-only credential projection | Inspected Pod/CronJob specs for names, env references, and volume references only. Cursor Subscription had `cursor` volume and no `CURSOR_API_KEY`; API-key resources had `CURSOR_API_KEY`/`cursor_api_key` and no `cursor` volume. No `ANTHROPIC_API_KEY`, `CODEX_API_KEY`, `claude`, or `codex` projection on Cursor sessions. `automountServiceAccountToken=false`. |
| Kubernetes + API | Claude Interactive still-start (2026-07-26 follow-up) | Session `8c4548f6b407` reached `phase=Running` on `open-agenthub-dev/agent-runtime-claude:local`. Deleted after smoke. |
| Kubernetes + API | Codex Interactive still-start (2026-07-26 follow-up) | Session `20b63b3d3f1b` / retry `b92b33492c1d` scheduled on `open-agenthub-dev/agent-runtime-codex:local` but Failed in init container `prepare-codex-system-config` (exit 127; requirements.toml treated as shell). Not claimed as a healthy Codex still-start. Deleted after diagnosis. |
| Automated test | Cursor runtime contracts | Driver, login-in-PTY, resume, MCP conversion, `cli-config` allowlists, and auth-watcher unit tests passed inside the 108 runtime suite. |
| Automated test | Backend/UI | Cursor agent kind, credential selection, pod factory, `cursor-state.tgz`, and frontend selectors passed inside the backend/frontend suites above. |

Primary synthetic matrix session IDs: `a4d1ab064f21`, `98d8f5540aab`, `6b634964800c`,
`eb49afeafc58`, `cfdc54cf470f`, `3f6df91198f5`. Follow-up smoke IDs: Claude
`8c4548f6b407`; Codex `20b63b3d3f1b` / `b92b33492c1d`. The write-only synthetic API key
string was `synthetic-cursor-api-key-not-real-acceptance-only` and was never treated as a
real secret.

## Design acceptance-criteria mapping (1–10)

Evidence is labeled **live** (Docker Desktop / API / kubectl on this cluster) or **unit**
(local automated suites). Rows that are unit-only are not claimed as live Docker Desktop
coverage.

| # | Criterion | Evidence |
| --- | --- | --- |
| 1 | Users can create Interactive, Autonomous, and Scheduled sessions with agent `Cursor` | **Live:** API matrix created all three modes; Pods/CronJob used the Cursor runtime image. |
| 2 | Users can choose Subscription or API-key auth independently per Cursor session | **Live:** Subscription Interactive and API-key Autonomous/Scheduled exercised; edit/duplicate round-tripped agent/auth. |
| 3 | Only the selected credential is present in the pod | **Live:** Pod/CronJob inspection of names, env refs, and volume refs only (no Secret values). |
| 4 | Subscription credentials persist per user across pods via the internal upload callback | **Unit only:** auth-watcher create/change/refresh tests. **Not live:** matrix did not wait out the watcher interval with an in-pod synthetic file rewrite, so Docker Desktop subscription-upload persistence is not claimed. |
| 5 | Autonomous/Scheduled fail closed when the selected credential is missing | **Live:** both missing-credential cases `phase=Failed` with no session Pod/CronJob. |
| 6 | Cursor policy maps to CLI `cli-config.json` permission tokens; automation is default-deny | **Unit only:** `cli-config` allow/deny mapping tests. **Not live:** allowed vs unmatched permission tokens were not exercised in a running Pod. |
| 7 | MCP config from AgentHub is translated for the Cursor CLI | **Unit only:** MCP conversion fixtures and unsupported-transport fail-closed tests. **Not live:** MCP conversion / fail-closed was not re-run as a live Pod before CLI start. |
| 8 | Pause/resume restores Cursor state without restoring stale credentials over newer Secrets | **Unit only:** backend/runtime archive key and resume-fallback contracts. **Not live:** no live `cursor-state.tgz` pause/resume round-trip on the cluster. |
| 9 | Helm, k8s manifests, setup-dev, and CI include the Cursor runtime image | **Live/local:** `setup-dev` built Cursor alongside backend, frontend, Claude, and Codex; ConfigMap exposed `AgentHub__CursorAgentImage`; `helm lint` and `cursor-runtime-values.ps1` passed. |
| 10 | Focused automated tests and a Docker Desktop acceptance report cover the above without real secrets in CI | **Partial:** local gates and the live matrix above passed without real secrets; this report maps each criterion and discloses unit-only / not-run gaps rather than claiming a full live checklist. |

## Limitations and user-owned follow-ups

- Real Cursor subscription authorization in the terminal was not completed (account-bound,
  optional, user-performed only).
- Auth-watcher create/refresh against a live Pod Secret was covered by unit tests; the
  live matrix did not wait out the watcher interval with an in-pod synthetic file rewrite
  (design AC 4 not live).
- Policy allow/deny tokens were not exercised in a live Pod (design AC 6 unit-only).
- MCP unsupported-transport failure before CLI start was not re-run as a live Pod
  (design AC 7 unit-only).
- Pause/resume with a live `cursor-state.tgz` round-trip was not exercised on the cluster
  (design AC 8 unit-only).
- Terminal reconnect / scrollback replay was **not run** on Cursor (or Claude/Codex) during
  this acceptance; no live reconnect evidence is claimed.
- Claude Interactive still-start was verified live on 2026-07-26 (`8c4548f6b407` Running on
  `agent-runtime-claude:local`). Codex Interactive still-start was attempted the same day
  but is **not** claimed healthy: the Pod used `agent-runtime-codex:local` and Failed in
  `prepare-codex-system-config` (exit 127). That Codex init failure is recorded as an
  environment/image finding outside Cursor credential projection; it was not fixed as part
  of this documentation follow-up.
- No separate browser UI checkpoint was recorded for this Cursor acceptance.
- Synthetic session resources from the 2026-07-25 matrix and the 2026-07-26 smokes were
  deleted afterward; the synthetic `cursorApiKey` status boolean was cleared after the
  matrix. Control-plane Pods in `agenthub-dev` remained healthy.

## Commands run

Non-secret commands actually executed for this acceptance (outcomes summarized; Secret
values never dumped into this report):

```powershell
# Local gates (2026-07-25) — all exited 0 with counts above
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
# frontend: npm test -- --run  → 21 files / 172 tests; npm run build → 81 modules
# session-agent: npm test → 108 passed
helm lint helm/open-agenthub -f helm/open-agenthub/values-dev.yaml --set-string postgres.password=test-only
pwsh -File tests/helm/cursor-runtime-values.ps1
git diff --check

# Cluster context gate + five-image deploy (2026-07-25)
kubectl config current-context   # → docker-desktop
.\setup-dev.ps1 -NoPortForward   # → five local images built/deployed; agenthub-dev healthy

# Image digests / ConfigMap presence (names and image IDs only)
docker image inspect <tag> --format '{{.RepoTags}} {{.Id}}'
kubectl -n agenthub-dev get configmap -o yaml   # filtered to *AgentImage keys

# Synthetic API matrix (2026-07-25) via temporary port-forward to agenthub-backend
kubectl -n agenthub-dev port-forward svc/agenthub-backend 18080:80
# REST create/get/patch/duplicate/delete against http://127.0.0.1:18080/api with
# header X-AgentHub-Test-User=cursor-acceptance (synthetic fixtures only)
kubectl -n agenthub-dev-sessions get pods,cronjobs -o wide
kubectl -n agenthub-dev-sessions get pod <name> -o json
# Inspection used only names, env/volume references, and key-presence metadata.

# Follow-up still-start smoke (2026-07-26); reconnect not attempted
kubectl config current-context   # → docker-desktop
# POST Interactive Claude + Codex Subscription sessions; GET phase; DELETE after
kubectl -n agenthub-dev-sessions get pod session-<id> -o jsonpath='{.spec.containers[0].image} {.status.phase}'
# Codex init diagnosis (non-secret): init logs for prepare-codex-system-config only
```

## Compatibility and security notes

Pod/CronJob inspection used only names, key-presence metadata, environment references, and
volume references for the acceptance claims above. This report does not include Secret
values. The Cursor feature work stayed on branch `feat/cursor-agent-support` in the
isolated worktree; the main checkout was not modified for this task.

Non-blocking advisories observed during gates:

- Existing KubernetesClient moderate advisory (`NU1902`).
- Vite chunk-size advisory above 500 kB.
- Helm recommended adding a chart icon.
