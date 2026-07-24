# Integrated On-Demand Browser Design

## Summary

Open AgentHub will provide every live agent session with an automatically configured
browser MCP server. The MCP server can request one isolated Chromium browser on demand,
control it through the Chrome DevTools Protocol (CDP), and expose the same visible
browser desktop to authorized users through noVNC.

No browser resources run until the agent invokes `browser_start`. A browser belongs to
exactly one agent session and is removed when explicitly stopped or when its owning
session is paused, finishes, or is deleted.

The first release covers the integrated browser only. An MCP server that creates other
Open AgentHub agent sessions is a separate future project.

## Goals

- Provide one lazily created Chromium browser per live agent session.
- Configure the built-in browser MCP automatically for both Claude and Codex sessions.
- Let the agent control the browser with high-level MCP tools and, when needed, raw CDP.
- Let authorized users see and interact with the same browser through the existing web
  application.
- Prevent any caller that merely knows a session ID from allocating or accessing a
  browser.
- Reserve no browser CPU or memory before the first browser MCP request.
- Persist cookies across browser restarts when S3-compatible storage is configured.
- Preserve the current session UI until a browser is actually requested.
- Support Helm, plain Kubernetes manifests, and Docker Desktop development.

## Non-goals

- WebRTC, TURN, audio streaming, or video-optimized streaming.
- A manual browser start or stop control in the user interface.
- More than one browser per agent session.
- Full Chromium profile persistence.
- Persistence of passwords, history, cache, downloads, extensions, or local storage.
- Copying browser state when a session is duplicated.
- A general MCP control plane for creating other Open AgentHub sessions.
- Browser download management or file transfer between the browser pod and workspace.

## User Experience

Before a browser exists, the session detail remains unchanged. There is no browser
button or empty browser placeholder.

When the agent invokes `browser_start`, the session's browser state changes to
`pending`. The frontend detects that state through its normal session refresh and
switches to a browser/agent split:

- Desktop starts at a resizable 50/50 split.
- The browser is on the left.
- The existing Agent, Shell, and Transcript experience remains on the right.
- Narrow layouts use Browser and Agent tabs instead of a horizontal split.
- The browser panel reconnects after a transient WebSocket interruption without
  stopping the browser pod.

The user and agent may interact with the browser concurrently. There is no exclusive
input lock. A human action may invalidate an MCP snapshot; the next MCP action then
returns a stale-reference error and instructs the agent to take another snapshot.

Shared-session permissions apply to the browser:

- Owners and collaborators with write access may send keyboard and pointer input.
- Viewers receive the stream with noVNC view-only mode enabled.
- Users without session access cannot open the browser WebSocket.

## Architecture

```text
Claude or Codex
      |
      | stdio MCP
      v
Built-in agenthub_browser MCP (inside the agent pod)
      |                         |
      | authenticated lifecycle | CDP 9222
      v                         v
Open AgentHub backend       Browser pod
      ^                         |
      | authenticated RFB proxy | websockify / RFB
      +-------------------------+
      |
Vue frontend with noVNC
```

### Built-in browser MCP

The MCP server is bundled into both agent runtime images and registered automatically
under the name `agenthub_browser`. It is a local stdio child process and opens no
listening network port.

The MCP receives the session ID, internal backend URL, and callback token through the
same runtime-owned environment boundary used by existing hooks. User-supplied MCP JSON
cannot replace, rename, or change the built-in server.

Interactive sessions use the provider's normal tool approval flow. Autonomous and
scheduled sessions must explicitly allow `mcp__agenthub_browser__*` or selected exact
browser tool names in their structured MCP policy.

### Browser control plane

The backend remains the only component with Kubernetes API credentials. It owns browser
leases, browser pod creation, readiness inspection, per-session NetworkPolicies, and
cleanup.

`browser_start` calls the internal lifecycle API. The backend atomically creates or
returns the single browser lease for the session. Repeated and concurrent calls are
idempotent.

### Browser data plane

The dedicated browser image contains pinned versions of:

- Chromium
- Xvfb
- x11vnc
- websockify
- a small browser supervisor used for readiness and cookie checkpoints

Chromium, CDP, Xvfb, VNC, and websockify run in one dedicated browser pod. CDP controls
the same visible Chromium process that x11vnc captures, so agent and user always operate
on the same tabs and page state.

The browser pod has no Kubernetes service-account token and does not share volumes,
process namespaces, credentials, or filesystem state with the agent pod.

### Frontend and WebSocket proxy

The Vue frontend embeds the `@novnc/novnc` RFB client. It connects only to an
authenticated Open AgentHub WebSocket:

`/ws/sessions/{id}/browser`

The backend resolves normal owner or shared-session access before opening the upstream
connection. It proxies the RFB stream to websockify in the matching browser pod. The
frontend never receives a pod IP, VNC password, or cluster-internal address.

## MCP Interface

The first release exposes these tools:

### `browser_start`

Creates or returns the browser assigned to the current agent session. It waits up to the
configured startup timeout for pod readiness, Chromium CDP readiness, and VNC readiness.

The result contains:

- `status`
- `screenWidth`
- `screenHeight`
- `cdpEndpoint`

The CDP endpoint is a cluster-internal WebSocket URL. NetworkPolicy permits it only from
the matching agent pod.

### `browser_status`

Returns one of `stopped`, `pending`, `running`, `stopping`, or `failed`, plus a sanitized
diagnostic when startup failed.

### `browser_stop`

Requests a final cookie checkpoint, terminates the browser pod, removes its dynamic
NetworkPolicies, and closes the lease. The operation is idempotent.

### `browser_navigate`

Navigates the selected tab to an HTTP or HTTPS URL.

### `browser_snapshot`

Returns a compact accessibility/DOM snapshot with references scoped to the current page
revision.

### `browser_click`

Clicks an element reference from the latest snapshot. A reference from an invalidated
page revision produces a stale-reference error.

### `browser_type`

Types text into a referenced element and optionally sends Enter. Text input is treated
as sensitive and must not be written to backend logs.

### `browser_screenshot`

Captures the selected tab and returns MCP image content. The screenshot is not
automatically persisted as an Open AgentHub artifact.

### `browser_tabs`

Lists tabs and can open, activate, or close a tab. The final remaining tab cannot be
closed without first opening a replacement.

## Internal and Public APIs

### Agent lifecycle API

The internal endpoints are:

- `POST /internal/sessions/{id}/browser`
- `GET /internal/sessions/{id}/browser`
- `DELETE /internal/sessions/{id}/browser`

They are not routed by the public ingress. Every call must pass all browser lifecycle
authorization checks described below.

### Browser state API

The existing session list and session detail responses gain a browser summary containing
only:

- `phase`
- `screenWidth`
- `screenHeight`
- a sanitized failure reason when applicable

The public response never contains callback tokens, browser-lease tokens, pod IPs, CDP
URLs, VNC addresses, or Kubernetes object names.

### Browser state callback

A browser pod receives an independent, random browser-lease credential. It can only:

- report readiness for its own lease;
- request fresh presigned GET and PUT URLs for its own cookie object.

It cannot create or stop browsers, call session-agent endpoints, mint arbitrary artifact
URLs, or modify session state. The backend stores a SHA-256 hash of the lease token and
compares token hashes in constant time.

## Lifecycle

The browser state machine is:

```text
stopped -> pending -> running -> stopping -> stopped
                    \-> failed
failed  -> pending  (a later start retries with fresh resources)
```

The backend creates the lease before Kubernetes resources. The database uniqueness
constraint on the session ID serializes concurrent starts. A caller that loses the race
reads and waits for the winning lease.

The backend creates:

1. the browser pod;
2. the CDP NetworkPolicy for the exact agent/browser pair;
3. the backend-to-VNC NetworkPolicy.

If any creation or readiness step fails, the backend records a sanitized failure,
removes partial Kubernetes resources, and leaves a failed lease that a subsequent start
may replace.

Cleanup occurs on:

- `browser_stop`;
- agent-session pause;
- terminal agent-session success or failure;
- agent-session deletion.

Browser cleanup is best effort and idempotent. Reconciliation on backend startup removes
orphaned browser resources whose session or active lease no longer exists.

## Authorization

Knowing a session ID is insufficient to create, inspect, or stop a browser.

Every agent lifecycle request must satisfy all of these conditions:

1. `X-Agent-Token` resolves to a live session record.
2. The resolved record's session ID exactly equals `{id}` in the route.
3. The session is currently backed by a live agent pod.
4. The request source IP equals the IP of a live Kubernetes pod carrying the exact
   session label.

The backend discovers eligible agent pods through the Kubernetes API. It does not trust
`X-Forwarded-For` or any caller-supplied pod identity header. A request that fails any
condition receives the same `401` response.

The built-in MCP itself is stdio-only. The callback token and returned CDP URL must be
redacted from structured logs, exception messages, traces, and metrics.

## Network Isolation

Browser and agent pods use a shared opaque session label generated by the backend.
NetworkPolicy selectors use that exact value.

Per active browser, dynamic policies allow:

- matching agent pod to matching browser pod on TCP 9222 for CDP;
- Open AgentHub backend pods to the matching browser pod on the websockify port;
- browser pod egress to DNS, HTTP, HTTPS, and explicitly configured extra browser egress
  ports.

The browser pod otherwise remains under the session namespace's default-deny ingress and
egress policy. No policy allows another agent pod to connect to CDP. Policies are
additive, so TCP 9222 is not added to the existing broad agent internet-egress rule.

The browser pod security context uses:

- a non-root UID and GID;
- `allowPrivilegeEscalation: false`;
- `capabilities.drop: [ALL]`;
- `seccompProfile.type: RuntimeDefault`;
- a read-only root filesystem;
- writable `emptyDir` mounts only for the Chromium profile, `/tmp`, and bounded
  `/dev/shm`;
- `automountServiceAccountToken: false`.

## Cookie Persistence

Cookie persistence is enabled only when the existing S3-compatible artifact store is
configured.

The browser supervisor:

1. requests a presigned GET URL through its lease-scoped callback;
2. downloads and validates the cookie JSON before first navigation;
3. imports valid cookies into Chromium;
4. exports cookies every configured checkpoint interval;
5. requests a fresh presigned PUT URL for each checkpoint;
6. performs a final best-effort checkpoint during graceful termination.

The cookie object key is:

`sessions/{owner-hash}/{sessionId}/browser-cookies.json`

The serialized data is capped at 1 MiB by default and contains only fields returned by
the browser cookie API. Invalid, oversized, or corrupt state is ignored with a
non-secret diagnostic and the browser starts clean.

Cookie upload failure never stops browsing or blocks pod termination. The previous
successful object remains available.

Cookie state survives browser stop, session pause, and session resume. It is deleted
when the session is deleted and is not copied by session duplication.

Deployments are responsible for enabling suitable S3 encryption at rest, retention, and
access controls because browser cookies may contain authenticated web sessions.

## Error Handling

- Startup has one bounded timeout covering Kubernetes scheduling and service readiness.
- Image pull, scheduling, Chromium, CDP, and VNC failures produce distinct sanitized
  failure codes.
- A noVNC disconnect affects only that viewer and does not stop the browser.
- A CDP disconnect returns an MCP transport error and directs the agent to call
  `browser_status`.
- MCP page references include a page revision and fail closed when stale.
- Lifecycle create, stop, cleanup, and reconciliation operations are idempotent.
- Backend restarts do not expose or duplicate a running lease.
- Browser state responses never echo tool input, typed text, cookies, URLs containing
  credentials, or internal endpoints.

## Configuration

The Helm chart exposes:

```yaml
browser:
  enabled: true
  image:
    repository: ghcr.io/open-agenthub/open-agenthub/browser
    tag: ""
    pullPolicy: IfNotPresent
  resources:
    requests:
      cpu: 250m
      memory: 512Mi
    limits:
      cpu: "1"
      memory: 2Gi
  screen: 1440x900
  startupTimeoutSeconds: 90
  cookieCheckpointSeconds: 60
  cookieStateMaxBytes: 1048576
  extraEgressPorts: []
```

The browser image tag defaults to the chart/application tag. Disabling the feature
removes the built-in MCP registration, browser RBAC permissions, and browser-specific
configuration while preserving all existing session behavior.

The backend's session-namespace Role gains only the verbs required for
`networkpolicies.networking.k8s.io`. Existing pod permissions cover browser pod
lifecycle. Plain manifests mirror the Helm behavior.

Docker Desktop development builds and loads the browser image and includes it in the
existing local smoke workflow.

## Testing

### Backend unit and integration tests

- Browser pod specs use the required non-root security context and bounded volumes.
- Dynamic policies select only the exact session's agent and browser pods.
- Concurrent start requests converge on one lease and one pod.
- Repeated stop and cleanup calls succeed.
- Partial Kubernetes creation is cleaned up.
- A missing token, wrong token, mismatched route ID, correct token from the wrong pod,
  and requests with spoofed forwarding headers all receive `401`.
- A correct token from the matching live pod can create and inspect its browser.
- Public session DTOs never serialize secrets or internal addresses.
- Cookie lease tokens are scoped, hashed, size-limited, and cannot invoke agent
  lifecycle operations.

### MCP tests

- Each tool validates its input and returns the documented result shape.
- Navigation, snapshots, clicks, typing, screenshots, and tab operations target the
  visible default Chromium context.
- Stale references fail closed.
- Lifecycle calls send the callback token only in the internal header.
- CDP and backend failures return sanitized errors.
- Cookie import and export enforce the configured size and schema limits.

### Frontend tests

- No browser control or placeholder appears for a stopped browser.
- Pending or running state automatically opens the split layout.
- Desktop starts at 50/50 and the divider is keyboard and pointer accessible.
- Narrow layouts expose Browser and Agent tabs.
- RFB reconnect does not issue a lifecycle start.
- Viewers use noVNC view-only mode and collaborators may send input.

### Deployment and smoke tests

- Helm renders valid resources with the browser enabled and disabled.
- The browser image starts Chromium, CDP, VNC, and websockify as non-root.
- The matching agent pod can reach CDP and a foreign agent pod cannot.
- The backend can reach RFB and no external ingress route reaches the browser pod.
- An end-to-end Docker Desktop test starts a session, invokes the browser MCP, navigates,
  observes the rendered frame, pauses the session, verifies cleanup, resumes, and
  verifies cookie restoration when S3-compatible storage is configured.

## Operational Notes

noVNC uses an RFB WebSocket and works through the existing HTTPS ingress without UDP or
TURN configuration. Playwright attaches to Chromium over CDP. The relevant upstream
interfaces are documented at:

- <https://novnc.com/noVNC/docs/API.html>
- <https://playwright.dev/docs/api/class-browsertype>

Neko is not used in the first release because its primary streaming transport is
WebRTC, while the accepted first-release transport is noVNC.

