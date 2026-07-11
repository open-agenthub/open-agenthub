# Open AgentHub

**Your coding agent shouldn't live on your laptop.**

Everybody uses coding agents — but only while their laptop is open. What about the other
16 hours of the day? The weekend? The time between meetings? claude.ai can't help there:
it has no access to your git workspaces, can't commit, can't run your tests, can't reach
company-internal resources.

Open AgentHub is the missing piece: **a secure place where your agent lives.** Each agent
runs 24/7 in an isolated Kubernetes pod with access to your work environment — your repos,
your credentials, your tools. Kick off a research task before you go to bed. Answer your
agent's questions from your phone between meetings. Schedule recurring jobs for the night
shift.

You stay the supervisor. **Be your own digital team lead.**

```
 Browser/Phone ──HTTPS/WSS──► Backend (C# / ASP.NET Core)  ──K8s API──► Agent pod
   Vue 3 + xterm.js            - Auth (OIDC, any provider)              - isolated, unprivileged
                               - Session orchestration                  - git + ssh + claude-code
                               - WS terminal proxy                      - session-agent (PTY+WS)
                                                                        - init container: git clone
                                                                        - secrets/MCP mounted
```

## Features

- **Interactive, autonomous, or scheduled sessions** — watch and answer live, hand off a
  prompt for unattended work, or run recurring jobs as CronJobs.
- **Mobile-first web UI** with live terminal streaming (xterm.js), reconnect with scrollback.
- **Bring your own container image** — run the agent in your project's toolchain image;
  Claude CLI, Node, and the terminal agent are copied in automatically.
- **Opt-in root mode** to install tools inside the container (apt, npm -g, …) while the pod
  stays unprivileged.
- **Subscription login that sticks**: log in once via `/login` inside a session; the OAuth
  credentials are stored per user and restored into every new session (incl. token refresh).
  Alternatively, use an `ANTHROPIC_API_KEY`.
- **Push notifications** when your agent has a question (via webhook, e.g. n8n → Slack/Push).
- **OIDC login** with any provider (Keycloak, Auth0, …), Authorization Code Flow + PKCE.

## Components

| Path | Contents |
|------|----------|
| `backend/` | ASP.NET Core: REST + WS proxy, K8s orchestration, JWT auth |
| `agent-runtime/` | Container image: runs Claude Code under a PTY, streams via WS |
| `frontend/` | Vue 3 + Vite + xterm.js, mobile-first |
| `helm/agenthub/` | Helm chart (recommended deployment) |
| `k8s/` | Plain manifests (namespaces, RBAC, backend, NetworkPolicies) |

## How it works

1. **Credentials** (SSH key, GitLab token, Anthropic key, known_hosts) are stored per user
   in a **dedicated Kubernetes Secret** (write-only through the UI).
2. **Start a session**: pick a mode, optionally a git repo (cloned into `/workspace/repo`
   by an init container), optionally an **MCP config** (placed as `.mcp.json` in the project).
   Optionally a **custom container image** (glibc-based; bash/git/curl recommended — the
   Claude CLI, Node, and the terminal agent are copied in by an init container) and
   **root mode** to install tools inside the container. The pod stays unprivileged.
3. The backend creates a **pod** (interactive/autonomous) or a **CronJob** (scheduled).
4. The `session-agent` runs `claude` under a **PTY** and serves a WebSocket with
   **scrollback** — reconnecting from your phone replays the history.
5. The browser only ever talks to the backend; the **WS proxy** forwards the stream to the
   pod, authenticated. Pods are unreachable from outside thanks to NetworkPolicies.

## Security

- **No root by default**: `runAsNonRoot`, UID 1000, `allowPrivilegeEscalation: false`,
  `readOnlyRootFilesystem`, `capabilities.drop: [ALL]`, `seccompProfile: RuntimeDefault`.
  **Root sessions are opt-in** (per session; disable via `agent.allowRootSessions=false`):
  UID 0 with a writable rootfs and default capabilities, but still no `privileged`, no
  `hostPath`, no privilege escalation; namespace PSA is then `baseline` instead of `restricted`.
- **Tenant isolation** via `owner` label; users only see/delete their own sessions.
- **Least-privilege RBAC**: the backend may only manage exactly the required objects in the
  session namespace; agent pods get **no** API token (`automountServiceAccountToken: false`).
- **NetworkPolicies**: default-deny; agent egress limited to DNS/HTTP(S)/SSH.
- **Dedicated PSA namespace** for sessions.
- Optionally harden further: set `RuntimeClassName` to gVisor/Kata (in `appsettings`/ConfigMap).

## Install (Helm, recommended)

Prebuilt images are published to `ghcr.io/open-agenthub/open-agenthub/*` and the chart
to our Helm repository:

```bash
helm repo add agenthub https://open-agenthub.github.io/open-agenthub
helm install agenthub agenthub/agenthub -n agenthub --create-namespace \
  --set postgres.password=<pw> \
  --set ingress.host=hub.your-org.example \
  --set oidc.authority=https://<oidc-provider>/realms/<realm>   # empty = auth off (dev mode!)
```

No cluster yet? The all-in-one quickstart installs k3s + Open AgentHub on a single host:

```bash
curl -fsSL https://open-agenthub.github.io/install.sh | sh
```

To build your own images instead (e.g. from a fork):

```bash
REG=registry.example.com/agenthub TAG=0.1.0
docker build -t $REG/backend:$TAG       ./backend
docker build -t $REG/frontend:$TAG      ./frontend
docker build -t $REG/agent-runtime:$TAG ./agent-runtime
docker push $REG/backend:$TAG && docker push $REG/frontend:$TAG && docker push $REG/agent-runtime:$TAG

# private registry? create the pull secret in BOTH namespaces and set image.pullSecret
helm upgrade --install agenthub helm/agenthub -n agenthub --create-namespace \
  --set image.registry=$REG --set image.tag=$TAG --set postgres.password=<pw>
```

All values (host, issuer, images, S3, OIDC) live in `helm/agenthub/values.yaml`; put
environment-specific overrides into your own values file (e.g. under `deploy/`, gitignored).
OAuth provider setup: public client `agenthub` with PKCE, redirect URI
`https://<host>/auth/callback`, post-logout `https://<host>`, web origins `https://<host>`.

## Build & deploy (plain manifests, without Helm)

```bash
# Build images
docker build -t registry.example.com/agenthub/backend:latest       ./backend
docker build -t registry.example.com/agenthub/agent-runtime:latest ./agent-runtime
docker push  registry.example.com/agenthub/backend:latest
docker push  registry.example.com/agenthub/agent-runtime:latest

# Cluster
kubectl apply -f k8s/00-namespace.yaml
kubectl apply -f k8s/10-rbac.yaml
kubectl apply -f k8s/40-postgres.yaml      # dev DB; use a managed DB in production
# Fill in secrets (Postgres password, S3 access/secret) BEFORE the backend starts:
#   k8s/20-backend.yaml  -> Secret "agenthub-secrets"
#   k8s/40-postgres.yaml -> Secret "postgres-secret"
kubectl apply -f k8s/20-backend.yaml
kubectl apply -f k8s/30-networkpolicy.yaml

# Frontend
cd frontend && npm install && npm run build   # serve dist/ behind an ingress/CDN
```

Local development: backend `dotnet run` (uses `~/.kube/config`), frontend `npm run dev`
(Vite proxies `/api` and `/ws` to `localhost:8080`). Leave `Oidc:Authority` empty to run
without auth (every request acts as user `dev`).

## Persistence, resume & notifications

No PVCs. Results flow back via `git push` or as artifacts to S3. What is persisted:

- **Postgres** = registry/status (source of truth for the session list). Table `sessions`
  with `claude_session_id`, `status`, `question_pending`, `callback_token`. The schema is
  created idempotently at backend startup.
- **S3/MinIO** = (1) Claude session state `claude-state.tgz` for real `--resume`,
  (2) `scrollback.log` for the history view of finished sessions, (3) `artifacts/...`.
  Layout: `sessions/{owner-hash}/{sessionId}/...`
- **No S3 credentials inside the pod**: the backend mints **presigned URLs** (PUT/GET,
  12 h TTL) and injects them as env vars. The agent uploads/downloads via `curl`. For
  arbitrary artifacts the agent calls `POST /internal/sessions/{id}/artifact-url?name=...`
  (token-authenticated). Without S3 configured, sessions still run — resume and history
  are simply disabled.

Resume flow: the backend deletes the old (finished) pod and starts a new one with
`AGENTHUB_RESUME=1` + a presigned GET for the state; `entrypoint.sh` extracts `~/.claude`,
the agent runs `claude --resume <claude_session_id>`. Same session ID = continuous history.

Push notifications on questions: a **Claude Code notification hook** (`notify-hook.sh`)
calls `POST /internal/sessions/{id}/notify`; the backend sets `question_pending=true` and
fires the webhook (`N8n:WebhookUrl`). The UI shows a blinking "waiting for your reply" dot.

Internal callback endpoints (`/internal/...`) use a per-session callback token (header
`X-Agent-Token`), no user auth, and are deliberately not routed through the ingress.
The NetworkPolicy only allows agent egress to the backend.

## Assumptions

- **Auth**: any OIDC provider works. Client `agenthub`, claim `preferred_username` as the
  tenant key.
- **Claude Code license**: via `ANTHROPIC_API_KEY` (in the user secret) **or subscription
  login**. With subscription login you log in once inside a session terminal (`/login`);
  the agent pod automatically backs up `~/.claude/.credentials.json` to the backend
  (dedicated user secret, incl. token refresh), and every new session restores it.
- **Autonomous mode** uses `--permission-mode acceptEdits` + a tool allowlist instead of
  "allow everything".
- **Claude Code CLI** supports `--session-id <uuid>` (pin the ID) and `--resume <uuid>` as
  well as the `Notification` hook event. If a flag/hook name ever changes, it's a one-line
  fix in `server.js` / `entrypoint.sh`.
- **S3 path-style** (MinIO). On real AWS, drop `ForcePathStyle`.

## Known limitations / next steps

- **Presigned TTL vs. long runners**: 12 h. If an autonomous session runs longer, a fresh
  PUT URL must be minted before expiry (small refresh endpoint) — noted as a limit today.
- **Artifact helper in the image**: `artifact-url` + upload exist; a convenient
  `s3put <file>` wrapper script is still missing.
- **Workspace state**: deliberately only via `git push` / artifacts; no file-level workspace
  resume (only chat/session resume via Claude state).
- **Job status detail view** for finished CronJob runs (`pods/log`).
- node-pty ABI: builder and runtime image must use the same Node major version (22 here).
- Custom images must be glibc-based (Debian/Ubuntu/Fedora…); Alpine/musl is not supported.

## License

Open AgentHub is an **open-core** project: the core (everything outside `ee/`) is free
software under the [GNU AGPL-3.0](LICENSE) and fully functional without a license key,
while the code in [`ee/`](ee/) is source-available under the commercial
[Open AgentHub Enterprise License](ee/LICENSE) and powers the enterprise offering
(6 €/user/month, 3-month free trial). Contributions require agreeing to our Contributor
License Agreement — see [CONTRIBUTING.md](CONTRIBUTING.md).
