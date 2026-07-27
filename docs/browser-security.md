# Browser operation and security

Open AgentHub provides a visible Chromium browser as a built-in, local stdio MCP server
named `agenthub_browser`. The browser is lazy: a session consumes no browser CPU or memory
until its agent calls `browser_start`. Exactly one browser lease and browser pod may be
active for a live session.

The browser desktop appears on the left of the session view after startup. Desktop layouts
use a resizable split and narrow layouts use tabs. The user and agent interact with the same
desktop. There are intentionally no manual browser start or stop controls in the UI; the
user asks the agent when a browser is needed.

## MCP tools

The managed MCP registration cannot be replaced by user MCP JSON. It exposes:

- `browser_start`, `browser_status`, and `browser_stop`
- `browser_navigate`, `browser_snapshot`, `browser_click`, and `browser_type`
- `browser_screenshot` and `browser_tabs`

Autonomous policies must explicitly allow
`mcp__agenthub_browser__*` or the required exact tool names.

## Authentication and network boundaries

Knowing a session ID is insufficient to create, inspect, or stop a browser. Every internal
browser lifecycle request must include the session callback token in `X-Agent-Token`.
The backend also resolves the TCP peer address and accepts the request only when it is the
IP of the exact, currently Running pod named for that session. Forwarding headers do not
participate in this decision. A foreign pod that copies public session labels is rejected,
even if it obtains the callback token.

When a lease is created, the backend creates dynamic NetworkPolicies for the exact
agent/browser pair:

- CDP on TCP 9222 is allowed only from the exact source IP of the bound Running
  agent pod; copied labels and a stolen callback token do not grant CDP access.
- websockify/RFB is allowed only from the Open AgentHub backend. Users connect through the
  authenticated session or shared-session WebSocket proxy; the browser pod is never
  exposed by an ingress or public Service. Viewer traffic uses a dedicated server-enforced
  view-only RFB endpoint. Active browser sockets are reauthorized every two seconds so a
  revoked link or downgraded collaborator loses the existing channel.
- Browser egress is limited to DNS, HTTP, HTTPS, the backend cookie-checkpoint endpoint,
  and explicitly configured extra ports.

Browser resources carry a random lease-generation label, and lifecycle operations are
serialized across backend replicas with a PostgreSQL advisory lock. Conflicting or stale
generations are rejected and cleaned.

Both the agent and browser pods disable service-account token mounting. The browser runs
as non-root with privilege escalation disabled, all Linux capabilities dropped, a
read-only root filesystem, and the RuntimeDefault seccomp profile.

Public session responses expose only browser phase, screen size, and a non-secret failure
code. They never expose callback or lease tokens, pod IPs, CDP URLs, presigned URLs, or
cookie contents.

## Cookie persistence

When S3-compatible storage is configured, the browser supervisor periodically checkpoints
cookies and makes a final best-effort checkpoint during shutdown. It restores valid
cookies before the browser becomes ready. Without S3, browsers still work and start with a
clean cookie jar.

The object key is:

```text
sessions/{owner-hash}/{sessionId}/browser-cookies.json
```

The versioned JSON object contains only a cookie array with Playwright cookie fields such
as name, value, domain, path, expiry, HTTP-only, secure, and same-site attributes. It does
not contain browsing history, cache, downloads, local/session storage, passwords, or a
Chromium profile. The backend enforces a 1 MiB default maximum. Invalid, oversized, or
corrupt state is ignored and Chromium starts clean.

The browser pod receives short-lived presigned GET/PUT URLs, not S3 credentials. Cookie
state survives browser stop, session pause, and resume. Deleting the session removes the
cookie object; duplicating a session does not copy it.

Cookies may represent authenticated web sessions. Production deployments must enable
encryption at rest for the S3 bucket, restrict bucket and backend access, configure
appropriate retention, and protect backups accordingly.

## Helm configuration

The feature is enabled by default but remains resource-free until used:

```yaml
browser:
  enabled: true
  image:
    repository: ghcr.io/open-agenthub/open-agenthub/browser
    tag: ""
    pullPolicy: IfNotPresent
  resources:
    requests: { cpu: 250m, memory: 512Mi }
    limits: { cpu: "1", memory: 2Gi }
  screen: 1440x900
  startupTimeoutSeconds: 90
  cookieCheckpointSeconds: 60
  cookieStateMaxBytes: 1048576
  extraEgressPorts: []
```

An empty image tag follows the chart/application tag. Set `browser.enabled=false` to remove
the MCP registration, browser-specific RBAC, and browser NetworkPolicy orchestration.
`extraEgressPorts` should remain empty unless a trusted workload needs another destination
port.

## Diagnostics

Start with the public browser summary and Kubernetes events:

```bash
kubectl -n <sessions-namespace> get pod browser-<session-id> -o wide
kubectl -n <sessions-namespace> describe pod browser-<session-id>
kubectl -n <sessions-namespace> get networkpolicy -l agenthub.dev/session=<session-id>
kubectl -n <control-namespace> logs deployment/<release>-backend --since=15m
```

Common failure codes:

- `pending`: scheduling or image startup has not completed. Inspect Pod conditions and
  events.
- `image_pull`: verify the browser repository/tag and `imagePullSecrets`.
- `unschedulable`: increase cluster capacity or reduce browser requests/limits.
- `cdp_unavailable`: Chromium did not become reachable; inspect browser pod logs and the
  session-scoped CDP policies.
- `vnc_unavailable`: inspect Xvfb/x11vnc/websockify startup and confirm backend-to-browser
  policy selection.

A noVNC disconnect does not stop Chromium. The UI reconnects independently, and the agent
can use `browser_status` to inspect lifecycle state. Session pause, completion, deletion,
or `browser_stop` removes the pod and its dynamic policies; reconciliation also cleans
orphans.

## Trust boundary

The browser executes untrusted web content and the agent can read and manipulate its
pages, cookies, and downloads during the live session. Only use trusted prompts, MCP
policies, repositories, and collaborators when the browser holds authenticated sessions.
NetworkPolicy limits reachability but does not make browser content or cookies secret from
the agent controlling that session.
