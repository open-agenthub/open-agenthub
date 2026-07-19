<div align="center">

<img src="frontend/public/favicon.svg" alt="Open AgentHub Logo" style="height: 150px"/>  
  
# Open AgentHub

**A home for your coding agent. Be your own digital team lead.**

[![Build](https://github.com/open-agenthub/open-agenthub/actions/workflows/build-images.yml/badge.svg)](https://github.com/open-agenthub/open-agenthub/actions/workflows/build-images.yml)
[![Helm chart](https://github.com/open-agenthub/open-agenthub/actions/workflows/release-chart.yml/badge.svg)](https://github.com/open-agenthub/open-agenthub/actions/workflows/release-chart.yml)
[![Coverage](https://codecov.io/gh/open-agenthub/open-agenthub/branch/main/graph/badge.svg)](https://codecov.io/gh/open-agenthub/open-agenthub)
[![Version](https://img.shields.io/github/v/release/open-agenthub/open-agenthub?include_prereleases&label=version&color=f2a33c)](https://github.com/open-agenthub/open-agenthub/releases)
[![License](https://img.shields.io/badge/license-AGPL--3.0%20+%20commercial%20ee-blue)](LICENSE)
[![Website](https://img.shields.io/badge/website-open--agenthub.github.io-f2a33c)](https://open-agenthub.github.io)

[Website](https://open-agenthub.github.io) ·
[Quick start](#quick-start) ·
[Features](#features) ·
[How it works](#how-it-works) ·
[Security](#security) ·
[Pricing](https://open-agenthub.github.io/#pricing)

</div>

---

Everybody uses coding agents — but only while their laptop is open. What about the other
**16 hours of the day**? The weekend? The time between meetings? Cloud chat AIs can't help
there: they have no access to your git workspaces, can't commit, can't run your tests,
can't reach company-internal resources.

Open AgentHub is the missing piece: **a secure place where your agent lives.** Each agent
runs 24/7 in an isolated Kubernetes pod with access to your work environment — your repos,
your credentials, your tools. Kick off a research task before you go to bed. Answer your
agent's questions from your phone between meetings. Schedule recurring jobs for the night
shift.

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
  prompt for unattended work, or run recurring jobs as CronJobs. Your agent works the
  night shift.
- **Supervise from anywhere** — mobile-first web UI with live terminal streaming
  (xterm.js); reconnect from your phone and the scrollback replays.
- **Bring your own container image** — run the agent inside your project's toolchain
  image; the Claude CLI, Node, and the terminal agent are copied in automatically.
- **Opt-in root mode** to install tools inside the container (apt, npm -g, …) while the
  pod stays unprivileged.
- **Bring your tools via MCP** — attach any MCP server (issue tracker, database,
  observability) per session and turn the agent into a teammate.
- **Community Edition projects and session duplication** — organize sessions into personal projects and duplicate reusable settings into an independent session without copying conversation state or credentials.
- **Subscription login that sticks** — log in once via `/login` inside a session; the
  OAuth credentials are stored per user and restored into every new session (incl. token
  refresh). Alternatively, use an `ANTHROPIC_API_KEY`.
- **Push notifications** when your agent has a question (via webhook, e.g. n8n → Slack).
- **Chat integrations** — session updates, replies, and permission approvals from your
  phone via **Telegram or Signal** (free, community) — Slack is part of the Enterprise
  edition — plus browser desktop notifications. [Setup below.](#chat-integrations)
- **OIDC login** with any provider (Keycloak, Entra ID, …), Authorization Code Flow + PKCE.
- **Security by default** — unprivileged pods, default-deny network policies, per-user
  secrets, no API tokens in agent pods. [Details below.](#security)

## Quick start

### Option A — you have a Kubernetes cluster

Prebuilt images come from `ghcr.io/open-agenthub/open-agenthub/*`; the chart from our
Helm repository:

```bash
helm repo add agenthub https://open-agenthub.github.io/open-agenthub
helm install agenthub agenthub/open-agenthub -n agenthub --create-namespace \
  --set postgres.password=$(openssl rand -hex 16) \
  --set ingress.host=hub.your-org.example
```

### Option B — you don't have Kubernetes

The all-in-one quickstart installs k3s (single node) + Open AgentHub on a Linux host
(recommended: 4 vCPU / 6 GB RAM — good for up to ~6 users):

```bash
curl -fsSL https://open-agenthub.github.io/install.sh | sh
```

### First steps after installation

1. **Open the UI.** With an ingress: `https://<your-host>`. Without one:
   `kubectl -n agenthub port-forward svc/agenthub-frontend 8080:80` → http://localhost:8080.
2. **Enable authentication** (auth is *disabled* by default — fine for a first test, not
   for anything reachable by others). See the
   [provider examples below](#configuring-oauthoidc-login), then:
   ```bash
   helm upgrade agenthub agenthub/open-agenthub -n agenthub --reuse-values \
     --set oidc.authority=<issuer-url> \
     --set oidc.clientId=<client-id> \
     --set oidc.audience=<expected-audience>
   ```
3. **Store your credentials** (Settings → Credentials): SSH key or GitLab/GitHub token
   for repo access, and an Anthropic API key — or leave it out and run `/login` inside
   your first session to use your Claude subscription (persists across sessions).
4. **Start your first session**: pick a repo, a mode (interactive / autonomous /
   scheduled), optionally a custom image and MCP config — and watch your agent work.

All configuration values (host, TLS issuer, images, S3, OIDC, resource limits) live in
[`helm/open-agenthub/values.yaml`](helm/open-agenthub/values.yaml). Optional S3/MinIO
credentials enable session resume, history of finished sessions, and artifact uploads.

### Configuring OAuth/OIDC login

Open AgentHub speaks standard OIDC: the frontend runs the Authorization Code Flow with
PKCE (public client, no secret), the backend validates the JWT access token against
`oidc.authority` / `oidc.audience`. The user's `preferred_username` claim is the tenant
key. Register the following in your provider:

- **Redirect URI:** `https://<host>/auth/callback`
- **Post-logout redirect URI:** `https://<host>`
- **Allowed web origins / CORS:** `https://<host>`

<details>
<summary><b>Keycloak</b></summary>

1. In your realm: **Clients → Create client** — Client ID `agenthub`, client type
   *OpenID Connect*, **Client authentication: Off** (public), Standard Flow enabled,
   PKCE method `S256` (Advanced → Proof Key for Code Exchange).
2. Set redirect URI / post-logout URI / web origins as above.
3. So the access token carries `aud=agenthub`: **Client scopes →
   agenthub-dedicated → Add mapper → Audience**, included client audience: `agenthub`.

```bash
helm upgrade agenthub agenthub/open-agenthub -n agenthub --reuse-values \
  --set oidc.authority=https://keycloak.example.com/realms/myrealm \
  --set oidc.clientId=agenthub \
  --set oidc.audience=agenthub
```
</details>

<details>
<summary><b>Microsoft Entra ID (Azure AD)</b></summary>

1. **App registrations → New registration** — name `agenthub`, supported account types
   as needed. Platform **Single-page application** with redirect URI
   `https://<host>/auth/callback` (SPA platform = PKCE without secret).
2. **Expose an API → Add a scope**: Application ID URI `api://<application-client-id>`,
   scope name e.g. `access`. (Without your own scope, Entra issues access tokens for
   Microsoft Graph, which Open AgentHub cannot validate.)
3. The frontend must request that scope:

```bash
helm upgrade agenthub agenthub/open-agenthub -n agenthub --reuse-values \
  --set oidc.authority=https://login.microsoftonline.com/<tenant-id>/v2.0 \
  --set oidc.clientId=<application-client-id> \
  --set oidc.audience=api://<application-client-id> \
  --set oidc.scope="openid profile email api://<application-client-id>/access"
```

Note: Entra ID puts the username into `preferred_username` (usually the UPN) — works
out of the box as the tenant key.
</details>

<details>
<summary><b>Google</b></summary>

1. **Google Cloud Console → APIs & Services → Credentials → Create OAuth client ID** —
   application type *Web application*, authorized JavaScript origin `https://<host>`,
   authorized redirect URI `https://<host>/auth/callback`.

```bash
helm upgrade agenthub agenthub/open-agenthub -n agenthub --reuse-values \
  --set oidc.authority=https://accounts.google.com \
  --set oidc.clientId=<client-id>.apps.googleusercontent.com \
  --set oidc.audience=<client-id>.apps.googleusercontent.com
```

⚠️ **Caveat:** Google issues *opaque* access tokens (not JWTs); only its **ID tokens**
are verifiable JWTs. Open AgentHub currently validates the access token, so Google login
requires the backend to validate the ID token instead — tracked as an open issue. Use
Keycloak or Entra ID for production today, or put Google behind Keycloak/Dex as an
identity broker (Keycloak's "Identity Providers → Google" works out of the box).
</details>

<details>
<summary><b>Build your own images / deploy without Helm</b></summary>

```bash
# Build & push images (e.g. from a fork)
REG=registry.example.com/agenthub TAG=0.1.0
docker build -t $REG/backend:$TAG       ./backend
docker build -t $REG/frontend:$TAG      ./frontend
docker build -t $REG/agent-runtime:$TAG ./agent-runtime
docker push $REG/backend:$TAG && docker push $REG/frontend:$TAG && docker push $REG/agent-runtime:$TAG

# private registry? create the pull secret in BOTH namespaces and set image.pullSecret
helm upgrade --install agenthub helm/open-agenthub -n agenthub --create-namespace \
  --set image.registry=$REG --set image.tag=$TAG --set postgres.password=<pw>
```

Plain manifests without Helm are available under [`k8s/`](k8s/) (namespaces, RBAC,
backend, network policies, dev Postgres) — fill in the secrets before applying.

</details>

### Chat integrations

Telegram and Signal are **community features** (free, no license); Slack is part of the
[Enterprise edition](ee/README.md). All of them notify you when a session waits for input
or finishes, and let you reply and approve permission prompts from your phone. Long
answers are split across several messages; permission prompts expire after ~30 minutes
(answer in the web terminal then).

<details>
<summary><b>Telegram</b></summary>

1. Create a bot via [@BotFather](https://t.me/BotFather) and copy the token.
2. Enable it:
   ```bash
   helm upgrade agenthub agenthub/open-agenthub -n agenthub --reuse-values \
     --set chat.telegram.enabled=true --set chat.telegram.botToken=<token>
   ```
   (Without Helm: env vars `Chat__Telegram__Enabled=true`, `Chat__Telegram__BotToken`.)
3. Each user links their account under **Settings → Notifications → Telegram**:
   **Generate link code**, then open the t.me deep link — or send `/link <code>` to the
   bot directly.

**Per-session forum topics** (optional): create a Telegram group, enable **Topics**, add
the bot as admin with the **Manage topics** permission, and send `/link <code>` in the
group — every session then gets its own topic. Note: **everyone in a linked group** can
reply to sessions and approve permission prompts.

Commands: `/sessions` (list), `/use <tag>` (route plain replies), `!status`. Plain
messages are typed into the active session's terminal. Only **one backend replica** may
run Telegram long-polling (a second poller conflicts on `getUpdates`).
</details>

<details>
<summary><b>Signal</b></summary>

1. Enable it — this deploys
   [signal-cli-rest-api](https://github.com/bbernhard/signal-cli-rest-api) with a
   persistent volume for the account keys:
   ```bash
   helm upgrade agenthub agenthub/open-agenthub -n agenthub --reuse-values \
     --set chat.signal.enabled=true --set-string chat.signal.number=+15551234567
   ```
2. Register the sender number once via port-forward: `POST /v1/register/<number>` +
   `/v1/register/<number>/verify/<token>` (SMS/voice code), or link it as a secondary
   device via QR code — the exact commands are in the comment block of
   [`helm/open-agenthub/templates/signal-cli.yaml`](helm/open-agenthub/templates/signal-cli.yaml).
3. Each user enters their number under **Settings → Notifications → Signal** and confirms
   the 6-digit verification code sent via Signal.

Reply routing: **quote** a session message to answer that session; plain replies go to
the newest session (or the one picked with `!use`). React 👍/👎 on a permission prompt to
allow/deny; quote-reply `always` for allow-always. Commands: `!sessions`, `!use <tag>`,
`!status`.
</details>

**Desktop notifications** (browser): a notification when a session waits for input or
finishes — enable per device under **Settings → Notifications**.

### Docker Desktop Kubernetes development

For a local Kubernetes environment, the setup scripts build the three images locally, deploy
the agenthub-dev Helm release into the agenthub-dev control namespace, and use
agenthub-dev-sessions for session pods. They refuse to run unless the active kubectl
context is docker-desktop.

On Windows PowerShell:

~~~
.\setup-dev.ps1
~~~

Use .\setup-dev.ps1 -NoPortForward when you want the checks and deployment without a
foreground port-forward. On Bash-compatible shells:

~~~
./setup-dev.sh
~~~

Use ./setup-dev.sh --no-port-forward for the same non-blocking behavior. The default
command checks the backend and frontend, then serves the UI at
http://localhost:8080 until you stop the port-forward. PostgreSQL uses ephemeral storage
and a generated password for this development release.

The development profile enables header-based test identities so local integration tests can
exercise owner, viewer, and collaborator behavior without an external identity provider.
This authentication is local-only, guarded by the Development environment, and must not
be enabled for a production deployment. The profile is defined in
helm/open-agenthub/values-dev.yaml.

To inspect or remove the release:

~~~
kubectl -n agenthub-dev get pods
helm uninstall agenthub-dev -n agenthub-dev
kubectl delete namespace agenthub-dev-sessions
~~~
### Local development

Backend: `cd backend && dotnet run` (uses `~/.kube/config`). Frontend:
`cd frontend && npm install && npm run dev` (Vite proxies `/api` and `/ws` to
`localhost:8080`). Leave `Oidc:Authority` empty to run without auth — every request acts
as user `dev`.

## Components

| Path | Contents |
|------|----------|
| `backend/` | ASP.NET Core: REST + WS proxy, K8s orchestration, JWT auth |
| `agent-runtime/` | Container image: runs Claude Code under a PTY, streams via WS |
| `frontend/` | Vue 3 + Vite + xterm.js, mobile-first |
| `helm/open-agenthub/` | Helm chart (recommended deployment) |
| `k8s/` | Plain manifests (namespaces, RBAC, backend, NetworkPolicies) |
| `ee/` | Enterprise features (commercial license, see below) |

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
- **Google login** requires ID-token validation (see the provider notes above) — open issue.
- Automated tests & coverage reporting are still being built out — the coverage badge will
  light up once the first test suite lands.

## Enterprise

Everything outside `ee/` is free forever (AGPL-3.0) and fully functional without a
license. The **Enterprise edition** adds features for teams — session sharing across your
org, org management, Slack integration — for **€6 / user / month** (excl. VAT) with a
**3-month free trial**. See [pricing](https://open-agenthub.github.io/#pricing) or the
[`ee/` README](ee/README.md).

## Contributing

Contributions are welcome! Please read [CONTRIBUTING.md](CONTRIBUTING.md) — contributions
require agreeing to our [CLA](.github/CLA.md) (dual licensing under AGPL-3.0 and the
commercial license keeps the open-core model possible).

## License

Open AgentHub is an **open-core** project: the core (everything outside `ee/`) is free
software under the [GNU AGPL-3.0](LICENSE) and fully functional without a license key,
while the code in [`ee/`](ee/) is source-available under the commercial
[Open AgentHub Enterprise License](ee/LICENSE) and powers the enterprise offering
(6 €/user/month excl. VAT, 3-month free trial). Contributions require agreeing to our Contributor
License Agreement — see [CONTRIBUTING.md](CONTRIBUTING.md).
