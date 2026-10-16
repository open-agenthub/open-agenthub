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

## Clipboard

A viewer who may control the browser (owner, collaborator, or a collaborator share link) can
copy and paste text between their own clipboard and the remote page. Ctrl/Cmd+V, C and X over
the browser pane are taken out of the VNC stream; the frontend calls
`/api/sessions/{id}/browser/clipboard/{paste,copy}` (or the `/api/shared/{token}/…` twin), and
the backend relays to the browser supervisor on TCP 6081, which only the backend can reach.
Both CDP connections preserve native focus rather than emulating it. With focus emulation,
every tab reports itself focused and visible: in 0.13.0 the supervisor could select the initial
blank tab, acknowledge the paste, and insert nothing into the page being viewed. The same
selection error affected copy. Keeping native focus lets the focused-frame check select the
page the viewer is controlling, including a field inside a frame.

The real browser regression runs paste, copy and cut over the supervisor HTTP route with
the initial blank tab still open, both CDP clients connected, and a focused field inside a
frame. CI runs it on a virtual display; locally run `npm test` in `browser-runtime` with
`AGENTHUB_TEST_BROWSER_EXECUTABLE` set to a browser executable and `DISPLAY` set to a
running X display. Without that executable setting, only this real browser test is skipped.

Paste inserts the text into the focused element with CDP `Input.insertText`. Copy reads the
selection of the focused frame, then lets Chromium run its own copy or cut command, so a cut
removes what it handed over and the page still sees the shortcut. Password fields yield
nothing, as they do for a native copy. Text is limited to 256 Ki characters and never logged.

The alternative was the RFB clipboard that x11vnc and noVNC already speak. It was rejected
because x11vnc sends every change of the X selection to all connected viewers, the view-only
endpoint included, so a copy made by one user would reach everybody watching. Both x11vnc
processes therefore run with `-nosel`, and the clipboard travels only per request, after the
same access check as keyboard input.

Text only: images and rich formats are not carried. Paste reads the local clipboard from the
browser's paste event, which needs no permission; only when the browser raises none is the
Clipboard API asked. Copy writes through the Clipboard API, and where the browser refuses
(insecure origin, denied permission) the pane says so instead of failing silently.

## WebGL

A browser pod has no GPU, and none is requested. Pages still get WebGL 1 and 2: Chromium is
started with `--enable-unsafe-swiftshader`, which lets it render WebGL on the CPU with the
software rasteriser bundled in its own package (reported to pages as
`ANGLE (…, Vulkan 1.3.0 (SwiftShader Device …), SwiftShader driver)`).

Without the switch, `canvas.getContext('webgl')` and `getContext('webgl2')` return `null`.
Chromium finds only the software OpenGL that the virtual X server brings along, runs its GPU
process on it, and blocklists WebGL there (`WebGL1 blocklisted` / `WebGL2 blocklisted` in the
pod log, `WebGL: Disabled` on `chrome://gpu`). Earlier versions then fell back to the bundled
rasteriser on their own; current ones do so only when asked. No library was missing — the
rasteriser and its Vulkan loader manifest are part of the pinned Chromium package for both
architectures, and the image build now fails if they ever are not.

### What "unsafe" means here

The switch is named that way because the rasteriser compiles shaders — which any page can
supply — to machine code and runs it in Chromium's GPU process, and upstream no longer wants
that reachable from the web without a decision. The decision here:

- It adds attack surface, not privilege. A page that breaks the shader compiler gets code
  execution in a process of the browser pod. Chromium's own sandbox is already off in this pod
  (`--no-sandbox`: its namespace sandbox needs privileges the pod does not have, and granting
  them would weaken the pod), so a compromised renderer was never confined by it either.
- What confines both is the pod, and that is unchanged: non-root, no capabilities, no
  privilege escalation, read-only root filesystem, RuntimeDefault seccomp, no service-account
  token, and the lease's NetworkPolicies. No device is mounted and nothing in the pod spec
  changes.
- What such a process can reach is what the [trust boundary](#trust-boundary) already assumes
  a hostile page may reach: the pages and cookies of that one session.

A deployment that would rather have no WebGL than this surface removes the switch from
`browser-runtime/entrypoint.sh` in its own image. There is no chart value for it.

### Cost

Only WebGL moves to the rasteriser. Compositing and raster of ordinary pages stay on the
software path they used before, so a page that never asks for a WebGL context costs what it
did. Measured with the image's Chromium on three cores, CPU time summed over all browser
processes:

| Start switches | Idle plain page, 30 s | Animated, scrolling plain page, 15 s |
|---|---|---|
| before (no WebGL) | 0.17–0.36 s | 1.8–2.0 s |
| `--enable-unsafe-swiftshader` | 0.22–0.41 s | 1.9–2.2 s |
| `--use-gl=angle --use-angle=swiftshader` | 0.20–0.35 s | 4.9–5.0 s |
| `--ignore-gpu-blocklist` | 0.31 s | 4.2 s |

A page that does draw pays for it: a full-canvas animated shader took about 0.2 cores at
300×150 and about 0.6 cores at 1280×720, at 55–60 frames per second. With the default limit of
one CPU, a heavy 3D scene will be slow rather than broken. Raise `browser.resources.limits.cpu`
for sessions that need more.

### Alternatives that were rejected

- **Leaving WebGL off.** Maps, charts, 3D viewers and many canvas libraries then fail with an
  error the agent cannot fix from inside the page, and the user watching the browser sees a
  blank widget.
- **`--use-angle=swiftshader` (with `--use-gl=angle`).** Also yields a context, and without
  the "unsafe" opt-in. But it puts *all* compositing and raster on the rasteriser, not just
  WebGL: 2.6 times the CPU on an ordinary animated page, paid by every session to serve the
  few pages that need WebGL. The shader compiler is exposed to pages just the same, so the
  nicer name buys nothing.
- **The system software OpenGL (llvmpipe) via `--ignore-gpu-blocklist`.** Works today, at 2.2
  times the CPU on ordinary pages for the same reason. It also depends on a library the image
  installs only as a transitive dependency of the X server, in whatever version the
  distribution carries, and the switch disables *every* blocklist entry, including those that
  exist for driver bugs. Installing a full system GL/Vulkan stack on purpose would grow the
  image for a renderer Chromium does not test against.
- **GPU passthrough.** It would need GPU nodes, a device plugin, a device mounted into a pod
  that runs untrusted web content, and a scheduler constraint on a pod meant to start anywhere
  within seconds. A kernel GPU driver is a far larger surface than a userspace rasteriser, and
  the feature would silently differ between nodes.

`browser-runtime/test/entrypoint.test.mjs` pins the switch and refuses the two CPU-heavy ones;
`browser-runtime/smoke.ps1` draws with WebGL 1 and 2 in the built image, read-only and as uid
1000, and checks the pixel.

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
