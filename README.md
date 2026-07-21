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
                               - Session orchestration                  - git + ssh + selected CLI
                               - WS terminal proxy                      - session-agent (PTY+WS)
                                                                        - Claude Code or Codex
                                                                        - selected secrets/MCP mounted
```

## Features

- **Interactive, autonomous, or scheduled sessions** — watch and answer live, hand off a
  prompt for unattended work, or run recurring jobs as CronJobs. Your agent works the
  night shift.
- **Claude or Codex per session** — choose the agent and Subscription or API-key billing
  independently in every mode. Migrated Claude sessions may retain internal legacy
  `Auto` authentication until explicitly changed; new sessions cannot select it.
- **Supervise from anywhere** — mobile-first web UI with live terminal streaming
  (xterm.js); reconnect from your phone and the scrollback replays.
- **Bring your own container image** — run the agent inside your project's toolchain
  image; the selected Claude or Codex runtime, Node, and terminal transport are copied
  in automatically.
- **Opt-in root mode** to install tools inside the container (apt, npm -g, …) while the
  pod stays unprivileged.
- **Bring your tools via MCP** — attach any MCP server (issue tracker, database,
  observability) per session and turn the agent into a teammate.
- **Community Edition projects and session duplication** — organize sessions into personal projects and duplicate reusable settings into an independent session without copying conversation state or credentials.
- **Subscription login that sticks** — sign in inside the selected provider container;
  refreshed Claude or Codex file-based authentication is persisted per user in the
  background. Codex uses its device-code flow in headless sessions. Host authentication
  files are never copied into the cluster by the setup scripts.
- **Push notifications** when your agent has a question (via webhook, e.g. n8n → Slack).
- **OIDC login** with any provider (Keycloak, Entra ID, …), Authorization Code Flow + PKCE.
- **Security by default** — unprivileged pods, default-deny network policies, per-user
  secrets, no Kubernetes service-account token in agent pods, and only the selected
  provider credential mounted or injected. [Details below.](#security)

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
   for repo access, plus an Anthropic or OpenAI API key if using API-key billing. These
   inputs are write-only; status responses expose only stored/not-stored booleans.
4. **Start your first session**: pick Claude or Codex, Subscription or API key, and a
   mode (interactive / autonomous / scheduled), plus any repo, custom image, policy, and
   MCP config. An Interactive Subscription session can complete provider login in its
   terminal; Codex uses `codex login --device-auth`.

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
docker build -f backend/Dockerfile -t $REG/backend:$TAG .
docker build -t $REG/frontend:$TAG ./frontend
docker build -f agent-runtime/claude/Dockerfile -t $REG/agent-runtime-claude:$TAG ./agent-runtime
docker build -f agent-runtime/codex/Dockerfile -t $REG/agent-runtime-codex:$TAG ./agent-runtime
docker push $REG/backend:$TAG && docker push $REG/frontend:$TAG
docker push $REG/agent-runtime-claude:$TAG && docker push $REG/agent-runtime-codex:$TAG

# image.registry/image.tag select all four defaults. Full runtime overrides are
# agent.images.claude and agent.images.codex. For a private registry, create the
# pull secret in BOTH namespaces and set image.pullSecret.
helm upgrade --install agenthub helm/open-agenthub -n agenthub --create-namespace \
  --set image.registry=$REG --set image.tag=$TAG --set postgres.password=<pw>
```

Plain manifests without Helm are available under [`k8s/`](k8s/) (namespaces, RBAC,
backend, network policies, dev Postgres) — fill in the secrets before applying.

</details>

### Docker Desktop Kubernetes development

For a local Kubernetes environment, the setup scripts build backend, frontend, Claude
runtime, and Codex runtime images locally, deploy the agenthub-dev Helm release into the
agenthub-dev control namespace, and use
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
| `agent-runtime/` | Separate Claude and Codex images sharing provider-neutral PTY/WS transport |
| `frontend/` | Vue 3 + Vite + xterm.js, mobile-first |
| `helm/open-agenthub/` | Helm chart (recommended deployment) |
| `k8s/` | Plain manifests (namespaces, RBAC, backend, NetworkPolicies) |
| `ee/` | Enterprise features (commercial license, see below) |

## How it works

1. **Credentials** (SSH key, Git token, provider API keys, and provider subscription
   files) are stored per user in general and provider-specific Kubernetes Secrets. API fields are
   write-only and credential values cannot be read back through the public API.
2. **Start a session**: pick agent, billing source, and mode. A repo may be cloned into
   `/workspace/repo`; optional MCP and structured policy configuration follows the
   selected provider. A custom glibc image can receive the selected provider runtime by
   init-container injection; bash, git, and curl are required.
3. The backend creates a **pod** (interactive/autonomous) or a **CronJob** (scheduled).
4. The shared transport runs `claude` or `codex` under a **PTY** and serves a WebSocket with
   **scrollback** — reconnecting from your phone replays the history.
5. The browser only ever talks to the backend; the **WS proxy** forwards the stream to the
   pod, authenticated. Pods are unreachable from outside thanks to NetworkPolicies.

## Agents, authentication, and policy

Agent and billing choices are independent for Interactive, Autonomous, and Scheduled
sessions. Subscription mode mounts only the selected provider's writable authentication
file; a background watcher persists valid login and refresh updates to that user's
provider-specific Secret. Claude login happens through its normal in-container flow;
Codex uses device-code authentication. Open AgentHub does not copy a workstation's real
Claude or Codex authentication files into the cluster.

API-key mode never mounts the subscription Secret. For provider authentication, Claude
scopes `ANTHROPIC_API_KEY` to the Claude process and its descendants. Codex
Autonomous/Scheduled runs scope `CODEX_API_KEY` to `codex exec` and its descendants, while
Interactive Codex creates an ephemeral file login from the key. The shared `/shell`
receives neither provider API key. This selected-only behavior makes the billing source
deterministic. Subscription avoids API-key billing, but it does not isolate credentials
from the running agent.
Autonomous and Scheduled sessions preflight the selected credential and fail before the
provider CLI starts when it is unavailable, with a non-secret diagnostic.


Automation policies default to deny and separately match built-in tools, MCP tool names,
and shell commands. Claude enforces exact structured shell commands through its native
allowed-tools mechanism plus AgentHub hooks; Codex matches normalized command prefixes with
a managed runtime-owned hook and workspace-write sandbox configuration. Interactive sessions
retain their normal provider approval flow and the existing out-of-band approval path. Hooks
are guardrails, not a complete sandbox or a secret-isolation boundary; pod, namespace,
RBAC, and NetworkPolicy isolation remain mandatory.

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
- **Kubernetes Secrets are not encryption by themselves**: their data is base64-encoded.
  Production clusters should enable API-server encryption at rest, preferably with KMS,
  restrict backend RBAC and namespace/network access, and rotate credentials regularly.

### Trusted-code credential boundary

Provider credentials and authentication files are accessible to code and tools running
as the same agent user. Claude and Codex provider-process descendants may inherit the
selected `ANTHROPIC_API_KEY` or `CODEX_API_KEY`, and subscription sessions can read their
selected provider auth file. Run only trusted repositories and prompts when credentials
are present. Use pod and network isolation to limit exposure and blast radius. Writable
collaborators can direct the selected session's capabilities and are part of the same
trust boundary.

Selected-only mounting reduces unnecessary exposure; it does not make provider
credentials secret from the selected agent or its tools. Root sessions expand the same
risk further. Policy hooks can reject unmatched actions but cannot provide credential
isolation. Subscription authentication changes the billing source, not this limitation.

## Persistence, resume & notifications

No PVCs. Results flow back via `git push` or as artifacts to S3. What is persisted:

- **Postgres** = registry/status (source of truth for the session list), including the
  selected agent, authentication mode, agent conversation identifier, status, policy,
  and callback metadata.
- **S3/MinIO** = provider-separated state (`claude-state.tgz` or `codex-state.tgz`),
  `scrollback.log`, and `artifacts/...`. State archives exclude provider authentication
  files; authentication restore happens after state restore so stale state cannot replace
  the current per-user login.
  Layout: `sessions/{owner-hash}/{sessionId}/...`
- **No S3 credentials inside the pod**: the backend mints **presigned URLs** (PUT/GET,
  12 h TTL) and injects them as env vars. The agent uploads/downloads via `curl`. For
  arbitrary artifacts the agent calls `POST /internal/sessions/{id}/artifact-url?name=...`
  (token-authenticated). Without S3 configured, sessions still run — resume and history
  are simply disabled.

Resume recreates the finished session resource with the same selected provider and
billing mode, restores only that provider's state, and then restores selected
authentication. Claude resumes its explicit conversation identifier. Codex resumes the
restored session-local thread and may fall back once to a fresh thread if state is absent
or invalid.

Provider runtime hooks call the internal notification endpoint when supported; the
backend sets `question_pending=true` and fires the configured webhook. The UI shows a
blinking "waiting for your reply" dot.

Internal callback endpoints (`/internal/...`) use a per-session callback token (header
`X-Agent-Token`), no user auth, and are deliberately not routed through the ingress.
The NetworkPolicy only allows agent egress to the backend.

## Assumptions

- **Auth**: any OIDC provider works. Client `agenthub`, claim `preferred_username` as the
  tenant key.
- **Provider access**: each user supplies their own Claude or Codex subscription login or
  API key. Open AgentHub does not issue subscriptions, tokens, or provider organization
  access.
- **Automation** uses a structured default-deny policy. Native provider controls and
  managed hooks supplement Kubernetes isolation; they do not replace it.
- **Provider CLI contracts** are pinned and tested by the separate runtime images. A
  provider CLI upgrade may require corresponding driver, hook, and resume changes.
- **S3 path-style** (MinIO). On real AWS, drop `ForcePathStyle`.

## Known limitations / next steps

- **Presigned TTL vs. long runners**: 12 h. If an autonomous session runs longer, a fresh
  PUT URL must be minted before expiry (small refresh endpoint) — noted as a limit today.
- **Artifact helper in the image**: `artifact-url` + upload exist; a convenient
  `s3put <file>` wrapper script is still missing.
- **Workspace state**: deliberately only via `git push` / artifacts; no file-level workspace
  resume (only provider conversation resume through provider-separated state).
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
