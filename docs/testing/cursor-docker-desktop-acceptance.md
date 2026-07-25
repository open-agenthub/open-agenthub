# Cursor Docker Desktop acceptance

Date: 2026-07-25
Base commit: `5e768e9c83137e7e1ddb22e09862474b5ea38749`
Kubernetes context: `docker-desktop`

## Result

Cursor support passed the full local regression suite, a five-image Docker Desktop
deployment via `setup-dev.ps1 -NoPortForward`, and an isolated API/Kubernetes matrix that
used only synthetic fixtures. No real Cursor account data, host credential storage,
browser storage, token, or Kubernetes Secret value was copied into the cluster or into
this report. Real account-bound Cursor subscription login remains a user action inside the
session terminal and was never completed.

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
| Automated test | Cursor runtime contracts | Driver, login-in-PTY, resume, MCP conversion, `cli-config` allowlists, and auth-watcher unit tests passed inside the 108 runtime suite. |
| Automated test | Backend/UI | Cursor agent kind, credential selection, pod factory, `cursor-state.tgz`, and frontend selectors passed inside the backend/frontend suites above. |

Primary synthetic matrix session IDs: `a4d1ab064f21`, `98d8f5540aab`, `6b634964800c`,
`eb49afeafc58`, `cfdc54cf470f`, `3f6df91198f5`. The write-only synthetic API key string was
`synthetic-cursor-api-key-not-real-acceptance-only` and was never treated as a real secret.

## Acceptance-criteria mapping

| Criterion | Evidence and outcome |
| --- | --- |
| Cursor selectable for Interactive / Autonomous / Scheduled | API matrix created all three modes; CronJob and Pods used the Cursor runtime image. |
| Subscription and API-key billing are independent | Subscription Interactive and API-key Autonomous/Scheduled exercised; selected-only mounts/env verified. |
| Missing selected credential fails Autonomous/Scheduled | Both missing-credential cases Failed with no session Pod/CronJob. |
| Unauthenticated Interactive reaches login | Live logs showed Cursor `login.sh` under the shared PTY. |
| Allowlists are not a sandbox | Documented in README; Cursor `cli-config` unit tests cover allow/deny mapping only. Kubernetes isolation remains the boundary. |
| Fifth image build/push/deploy | `setup-dev` built Cursor alongside backend, frontend, Claude, and Codex; ConfigMap exposed `AgentHub__CursorAgentImage`. |
| No real Cursor credentials | No host Cursor credential file was read or copied. Only synthetic API-key material was stored for the matrix. |
| Unit/component/runtime/build/Helm/Docker Desktop | Local gates and the five-image Docker Desktop path above passed. |

## Limitations and user-owned follow-ups

- Real Cursor subscription authorization in the terminal was not completed (account-bound,
  optional, user-performed only).
- Auth-watcher create/refresh against a live Pod Secret was covered by unit tests; the
  live matrix did not wait out the watcher interval with an in-pod synthetic file rewrite.
- Pause/resume with a live `cursor-state.tgz` round-trip was not exercised on the cluster;
  archive key and resume-fallback contracts are covered by backend/runtime tests.
- MCP unsupported-transport failure before CLI start was not re-run as a live Pod; runtime
  MCP tests already fail closed for unsupported transports.
- No separate browser UI checkpoint was recorded for this Cursor acceptance.
- Synthetic session resources from this matrix were deleted and the synthetic
  `cursorApiKey` status boolean cleared afterward. Control-plane Pods in `agenthub-dev`
  remained healthy.

## Compatibility and security notes

Pod/CronJob inspection used only names, key-presence metadata, environment references, and
volume references for the acceptance claims above. This report does not include Secret
values. The Cursor feature work stayed on branch `feat/cursor-agent-support` in the
isolated worktree; the main checkout was not modified for this task.

Non-blocking advisories observed during gates:

- Existing KubernetesClient moderate advisory (`NU1902`).
- Vite chunk-size advisory above 500 kB.
- Helm recommended adding a chart icon.
