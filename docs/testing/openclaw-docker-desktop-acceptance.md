# OpenClaw Docker Desktop acceptance

Date: _pending live matrix_
Base commit: _fill after acceptance run_
Kubernetes context: `docker-desktop`

## Result

This document is the acceptance sketch for OpenClaw CE support. It mirrors the Cursor
acceptance layout so a later Docker Desktop matrix can record evidence without inventing
a new checklist. **No live Docker Desktop acceptance matrix has been completed for
OpenClaw yet.** Claims below mark what wiring and automated gates already cover versus
what still needs a live cluster run.

No real OpenClaw account data, host credential storage, browser storage, token, or
Kubernetes Secret value should be copied into the cluster or into this report. Real
account-bound OpenClaw subscription login remains a user action inside the session
terminal.

## Environment and local gates

| Tool | Version |
| --- | --- |
| .NET SDK | _record at acceptance time_ |
| Node.js | _record at acceptance time_ |
| npm | _record at acceptance time_ |
| Helm | _record at acceptance time_ |
| kubectl client | _record at acceptance time_ |
| Docker client/server | _record at acceptance time_ |
| OpenClaw CLI (image) | pinned in `agent-runtime/openclaw/Dockerfile` (`OPENCLAW_VERSION`) |

Local verification expected before a live matrix:

- Backend / frontend / session-agent unit suites green (includes OpenClaw models, pod
  factory, driver, auth watcher).
- Helm: `helm lint` on the development chart; `tests/helm/openclaw-runtime-values.ps1`
  (default, dev, fallback, override); `tests/helm/deployment-parity.ps1`.
- `git diff --check` clean for touched paths.

## Images and deployment

`kubectl config current-context` must be exactly `docker-desktop`. The supported
`.\setup-dev.ps1 -NoPortForward` path builds and deploys all **six** images:

| Tag | Local image ID |
| --- | --- |
| `open-agenthub-dev/backend:local` | _record_ |
| `open-agenthub-dev/frontend:local` | _record_ |
| `open-agenthub-dev/agent-runtime-claude:local` | _record_ |
| `open-agenthub-dev/agent-runtime-codex:local` | _record_ |
| `open-agenthub-dev/agent-runtime-cursor:local` | _record_ |
| `open-agenthub-dev/agent-runtime-openclaw:local` | _record_ |

ConfigMap keys must include:

- `AgentHub__ClaudeAgentImage: open-agenthub-dev/agent-runtime-claude:local`
- `AgentHub__CodexAgentImage: open-agenthub-dev/agent-runtime-codex:local`
- `AgentHub__CursorAgentImage: open-agenthub-dev/agent-runtime-cursor:local`
- `AgentHub__OpenClawAgentImage: open-agenthub-dev/agent-runtime-openclaw:local`

## Acceptance matrix (planned)

| Evidence surface | Case | Outcome |
| --- | --- | --- |
| Kubernetes + API | OpenClaw Interactive / Subscription without stored auth | _pending_ — expect Running/Ready on `agent-runtime-openclaw:local`, subscription volume only, login path in PTY; real login not completed |
| Kubernetes + API | Missing OpenClaw Subscription for Autonomous | _pending_ — expect `phase=Failed`, no Pod |
| Kubernetes + API | Missing selected API key for Scheduled (per source) | _pending_ — expect `phase=Failed`, no CronJob/Pod |
| Kubernetes + API | OpenClaw Autonomous / ApiKey with synthetic Anthropic/OpenAI/Cursor key | _pending_ — selected-only env; no OpenClaw subscription volume |
| Kubernetes + API | OpenClaw Scheduled / ApiKey | _pending_ — CronJob shape + selected-only key |
| Kubernetes/API | Edit and duplicate | _pending_ — round-trip OpenClaw + AuthMode + OpenClawApiKeySource |
| Kubernetes | Selected-only credential projection | _pending_ — Subscription mounts `openclaw` only; ApiKey mounts only chosen key |
| Automated test | OpenClaw runtime contracts | Driver, resume, auth-watcher unit coverage in session-agent suite |
| Automated test | Backend/UI | OpenClaw agent kind, api-key source, pod factory, `openclaw-state.tgz`, frontend selectors |
| Helm / CI | Image wiring | **Local:** Helm helper/ConfigMap, `values-dev`, plain `k8s/20-backend.yaml`, `setup-dev`, `build-images.yml`, and `test.yml` include `agent-runtime-openclaw` / `OpenClawAgentImage` |

## Design acceptance-criteria mapping

| # | Criterion | Evidence |
| --- | --- | --- |
| 1 | Users can create Interactive, Autonomous, and Scheduled sessions with agent `OpenClaw` | **Pending live**; unit coverage exists |
| 2 | Users can choose Subscription or API-key auth (with source when ApiKey) independently | **Pending live**; unit/UI coverage exists |
| 3 | Only the selected credential is present in the pod | **Pending live**; pod-factory unit tests exist |
| 4 | Subscription credentials persist per user via internal upload callback | **Unit:** auth-watcher; **not live** until matrix waits out watcher interval |
| 5 | Autonomous/Scheduled fail closed when the selected credential is missing | **Pending live**; backend diagnostic coverage exists |
| 6 | Policy / MCP behavior for OpenClaw where supported | **Unit / pending live** as applicable |
| 7 | Pause/resume restores OpenClaw state without restoring stale credentials over newer Secrets | **Unit / pending live** (`openclaw-state.tgz`) |
| 8 | Helm, k8s manifests, setup-dev, and CI include the OpenClaw runtime image | **Done in Task 7 wiring** — verify with helm parity scripts |
| 9 | Focused automated tests and this acceptance report cover the above without real secrets in CI | **Partial** until live matrix is filled in |

## Limitations and user-owned follow-ups

- Live Docker Desktop matrix not yet run; fill image digests, session IDs, and outcomes above.
- Real OpenClaw subscription authorization in the terminal is optional and user-performed only.
- Do not dump Secret values into this report; inspect names, env refs, and volume refs only.
- Synthetic fixtures only (for example `synthetic-openclaw-api-key-not-real-acceptance-only`).

## Commands to run

```powershell
# Local gates
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
# frontend: npm test -- --run; npm run build
# session-agent: npm test
helm lint helm/open-agenthub -f helm/open-agenthub/values-dev.yaml --set-string postgres.password=test-only
pwsh -File tests/helm/openclaw-runtime-values.ps1
pwsh -File tests/helm/deployment-parity.ps1
git diff --check

# Cluster context gate + six-image deploy
kubectl config current-context   # → docker-desktop
.\setup-dev.ps1 -NoPortForward

# Image digests / ConfigMap presence (names and image IDs only)
docker image inspect <tag> --format '{{.RepoTags}} {{.Id}}'
kubectl -n agenthub-dev get configmap -o yaml   # filtered to *AgentImage keys

# Synthetic API matrix via temporary port-forward to agenthub-backend
kubectl -n agenthub-dev port-forward svc/agenthub-backend 18080:80
# REST create/get/patch/duplicate/delete against http://127.0.0.1:18080/api with
# header X-AgentHub-Test-User=openclaw-acceptance (synthetic fixtures only)
kubectl -n agenthub-dev-sessions get pods,cronjobs -o wide
```

## Compatibility and security notes

Pod/CronJob inspection must use only names, key-presence metadata, environment references,
and volume references. This report must not include Secret values. OpenClaw ApiKey mode
reuses existing Anthropic/OpenAI/Cursor keys; Subscription uses `openclaw-{owner}` only.
