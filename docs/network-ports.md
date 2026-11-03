# Dynamic network port requests

Session pods run in a **default-deny** namespace: a `NetworkPolicy` blocks all ingress
and egress, and only narrow static allowances exist (DNS, HTTP/S, SSH for git, the
backend callback, and the browser/preview pairs). That is the right default for
untrusted agent workloads — but it blocks legitimate tasks like connecting to a
Postgres instance on 5432 or letting the session browser open a dev server the agent
runs on a non-default port.

Instead of widening the static policy for everyone, an agent can ask for a specific
port **at runtime** and the session owner decides.

## How it works

1. Every session pod gets the built-in `agenthub_network` MCP server
   (`agent-runtime/network/`, enabled via `AGENTHUB_NETWORK_MCP_ENABLED`). It exposes:
   - `port_request { direction, port, protocol?, reason }` — ask for a port and wait
     for the decision (direction `egress` or `browser_to_agent`).
   - `port_list` — the ports already opened for this session.
2. The server calls `POST /internal/sessions/{id}/network/port-requests`,
   authenticated with the per-session callback token (`X-Agent-Token`) like every
   other internal endpoint. It cannot act for another session.
3. The backend checks the **allowlist first** (see below). A port outside it is denied
   immediately — no one is ever asked, so the user cannot be talked into opening
   arbitrary ports.
4. Allowed requests become a pending entry on the standard **permission channel**
   (the same one tool permissions use). The owner sees an inline banner in the session
   view — "The agent asks to open network port 5432 (outgoing traffic, TCP)." with the
   agent's reason — and can Allow / Allow (don't ask again) / Deny. Messenger prompts
   (Slack, Telegram, Signal) work unchanged, because a port request is stored exactly
   like a tool-permission request.
5. The agent polls the decision. On the first poll that sees an approval, the backend
   creates the `NetworkPolicy` objects and records the grant (Postgres, so any replica
   answers consistently). If nobody decides within the timeout (default 5 minutes),
   the request expires and the tool returns a clear message; an approval that raced
   the expiry is still honored.

## What gets created

All policies are **additive** on top of default-deny, scoped to this session's pods by
label selectors, and carry `agenthub.dev/session` plus
`agenthub.dev/network-request=true`:

- `egress` — one policy on the agent pod
  (`session-<id>-net-egress-<proto>-<port>-out`) allowing outbound traffic to the port
  anywhere. Example: Postgres 5432.
- `browser_to_agent` — a matching pair like the preview-port pattern:
  ingress on the agent pod from the session's browser pod
  (`…-in`) and egress on the browser pod to the agent pod (`…-out`), TCP only.
  Example: the agent serves an app on 9000 and shows it in the session browser.

Policy names are deterministic, so re-approvals and replica races collapse into a
create-conflict no-op instead of duplicates.

## Security model

- **Default deny stays.** Nothing is opened without an explicit approval.
- **Instance allowlist.** Only ports the operator listed can even be requested:

  ```yaml
  # helm/open-agenthub/values.yaml
  agent:
    portRequests:
      enabled: true
      requestablePorts:
        - "3000-3010"   # React, Next.js, Nuxt, Node
        - "3306"        # MariaDB / MySQL
        - "3900"        # Garage S3 API
        - "4173"        # Vite preview
        - "4200-4210"   # Angular
        - "5000-5300"   # ASP.NET Core http, Flask, Vite dev (5173)
        - "5432"        # PostgreSQL
        - "6379"        # Redis
        - "6443"        # Kubernetes API
        - "7000-7300"   # ASP.NET Core https
        - "8000-8010"   # Django, FastAPI, Laravel, PHP built-in server
        - "8080-8090"   # generic http
        - "9000-9100"   # MinIO, php-fpm
        - "27017"       # MongoDB
  ```

  Entries are single ports or `from-to` ranges; `[]` disables requests entirely (as
  does `enabled: false`). The list is rendered into the backend config as a single
  string (`Network__RequestablePorts`), so clearing it really clears it. Invalid
  entries are dropped, never widened.

  **Why the defaults are broad.** Being on the list is permission to *ask*; the owner
  still approves each request. A port that is missing is refused before the owner sees
  the question, and an agent cannot tell that apart from the platform being unable to do
  it — so it stops asking and works around the gap. The defaults therefore cover the
  backing services an agent plausibly needs and the dev server each framework starts on
  its own, rather than the minimum.

  `6443` is on the list and is not the escalation it looks like: session pods set
  `automountServiceAccountToken: false`, as does the service account, so a pod holds no
  cluster credential. Reaching an API server needs a kubeconfig the session was given
  deliberately.
- **User approval.** Requests inside the allowlist still require the session owner's
  decision (unless the session runs with auto-approve, which the owner opted into).
  "Allow (don't ask again)" is scoped to the exact direction + port + protocol of one
  session.
- **Session scoping.** The `X-Agent-Token` binds requests to their session; policies
  select only this session's pods; grants live per session id.
- **Automatic cleanup.** When the session is deleted, `KubernetesSessionService`
  removes all `agenthub.dev/network-request=true` policies of the session (label
  selector delete) and drops the grant rows. Scheduled sessions never get the MCP
  server (nobody would be around to approve).

## Limits

- Egress grants open the port to *any* destination (like the static
  `agent.extraEgressPorts`), not to a specific host — NetworkPolicies are L3/L4 and
  per-IP scoping of dynamic external services is not practical here.
- Grants last for the lifetime of the session; there is no per-grant revoke UI yet
  (delete the session to remove them).


## Automatic object-storage egress

The chart opens session-pod egress for the effective storage endpoint. State restore
and state, scrollback, transcript and `upload_file` uploads still use presigned URLs
from the pod. Blocking that path can silently leave resumed sessions without saved
state even though attachment reads through the backend succeed.

With `objectStorage.enabled: true` and no `s3.serviceUrl` override, the additional
policy permits TCP 3900 only to `app: garage` pods in the release namespace. An
explicit `s3.serviceUrl` takes precedence, even while bundled storage is enabled.
Its explicit port is used, or 443 for HTTPS and 80 for HTTP. Literal IP endpoints
use a /32 or /128 `ipBlock`; DNS endpoints on non-standard ports require an explicit
`agent.storageEgress.cidrs` list; otherwise no extra policy is added and install notes
warn that session storage needs configuration. Standard NetworkPolicy cannot select
a hostname. `agent.extraEgressPorts` remains a deliberate opt-in to allow a port to
every destination. Keep configured CIDRs current when storage addresses change. Resolving DNS during chart rendering
was rejected because addresses can change after installation.

With bundled storage disabled and an empty service URL, no storage policy is added;
the existing HTTPS allowance covers the default storage endpoint. URLs on 80/443
also need no additional policy. Existing 80/443
allowances remain additive, so a new policy cannot narrow those ports. The
`agent.extraEgressPorts` override remains available, including when a Service translates
the URL's port to a different destination container port. Standard NetworkPolicy
matches pod ports after Service translation; automatic inference cannot discover an
external Service's target port.

### Rejected

Routing state restore and all uploads through the backend would remove direct storage
egress but increase backend bandwidth and memory pressure. Keep direct presigned
transfers for now and open the required storage path automatically. Opening bundled
Garage's port globally was rejected because its namespace and pod labels are known.
