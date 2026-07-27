# Dynamic Browser Viewport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resize the Xvfb desktop and Chromium window to the owning AgentHub browser pane so the complete browser fills the pane without crop, distortion, scrollbars, or VNC reconnects.

**Architecture:** `BrowserPane` debounces its measured canvas dimensions and calls an owner-only API. The backend validates the active browser lease and forwards the request through a focused runtime client to the browser supervisor on port 6081. The supervisor serializes XRandR and CDP window updates; x11vnc announces the new framebuffer size to the existing noVNC connection.

**Tech Stack:** Vue 3, Vitest, noVNC, ASP.NET Core 10, xUnit, Kubernetes NetworkPolicy, Node.js 22, Playwright CDP, Xvfb, XRandR, x11vnc.

## Global Constraints

- Only a normal authenticated owner view may request a remote viewport resize; shared-token views never get a resize route.
- Accept integer dimensions from `480 × 320` through `2560 × 1600`, inclusive.
- Debounce frontend measurements by 250 milliseconds and suppress duplicate dimensions.
- Keep the existing noVNC WebSocket open during resize.
- Use proportional contain scaling while the remote framebuffer catches up and for all shared views.
- Port `6081` is reachable only from backend pods in the control namespace.
- Resize failures preserve the browser process and current VNC connection.
- Do not add unrelated refactors or a user-facing resize control.

---

### Task 1: Browser runtime viewport engine

**Files:**
- Modify: `browser-runtime/supervisor.mjs`
- Modify: `browser-runtime/test/supervisor.test.mjs`

**Interfaces:**
- Produces: `validateViewport(value): { width: number, height: number }`
- Produces: `BrowserSupervisor.resizeViewport(width, height): Promise<void>`
- Produces: `PUT /viewport` on the existing supervisor server at port `6081`
- Consumes: `context.pages()`, `context.newCDPSession(page)`, and injected `execFile`

- [ ] **Step 1: Read the test-quality rules**

Read `superpowers/test-driven-development/writing-good-tests.md` completely before editing tests.

- [ ] **Step 2: Write failing validation and resize-order tests**

Add tests that assert:

```js
assert.deepEqual(validateViewport({ width: 800, height: 600 }), { width: 800, height: 600 });
assert.throws(() => validateViewport({ width: 479, height: 600 }), /viewport/i);
assert.throws(() => validateViewport({ width: 800.5, height: 600 }), /viewport/i);

await supervisor.resizeViewport(800, 600);
assert.deepEqual(events, [
  ['xrandr', ['--display', ':99', '--fb', '800x600']],
  ['cdp', 'Browser.getWindowForTarget'],
  ['cdp', 'Browser.setWindowBounds', {
    windowId: 7,
    bounds: { left: 0, top: 0, width: 800, height: 600, windowState: 'normal' }
  }]
]);
```

Use injected `execFile` and a fake CDP session. Add a second test where `execFile` rejects and prove that CDP is not called and the supervisor remains usable for a later request.

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
npm test -- --test-name-pattern "viewport|resize"
```

from `browser-runtime`.

Expected: FAIL because `validateViewport` and `resizeViewport` do not exist.

- [ ] **Step 4: Implement bounded validation and serialized resize**

In `supervisor.mjs`:

```js
import { execFile as execFileCallback } from 'node:child_process';
import { promisify } from 'node:util';

const execFile = promisify(execFileCallback);
export const MIN_VIEWPORT = { width: 480, height: 320 };
export const MAX_VIEWPORT = { width: 2560, height: 1600 };

export function validateViewport(value) {
  const width = value?.width;
  const height = value?.height;
  if (!Number.isInteger(width) || !Number.isInteger(height) ||
      width < MIN_VIEWPORT.width || height < MIN_VIEWPORT.height ||
      width > MAX_VIEWPORT.width || height > MAX_VIEWPORT.height) {
    throw new Error('Viewport dimensions are invalid');
  }
  return { width, height };
}
```

Add `this.execFile = options.execFile ?? execFile` and `this.resizeTail = Promise.resolve()` in the constructor. Implement `resizeViewport()` by chaining a private `resizeViewportOnce()` onto `resizeTail`. The private operation must:

1. run `xrandr --display :99 --fb WIDTHxHEIGHT`;
2. select the first existing context page;
3. create a CDP session;
4. send `Browser.getWindowForTarget`;
5. send `Browser.setWindowBounds` with the exact dimensions;
6. detach the CDP session in `finally`.

- [ ] **Step 5: Add failing HTTP endpoint tests**

Start the supervisor health server on an ephemeral test port or invoke an exported request handler. Prove:

```js
assert.equal((await putViewport({ width: 800, height: 600 })).status, 204);
assert.equal((await putViewport({ width: 200, height: 600 })).status, 400);
assert.equal((await putViewport('{broken')).status, 400);
assert.equal((await putViewport({ width: 800, height: 600 }, failingSupervisor)).status, 500);
```

Also prove that two concurrent valid requests execute sequentially.

- [ ] **Step 6: Run HTTP tests and verify RED**

Run:

```powershell
npm test -- --test-name-pattern "viewport"
```

Expected: FAIL because `/viewport` is not handled.

- [ ] **Step 7: Implement the bounded PUT handler**

Extract a focused request handler used by `start()`:

```js
if (request.method === 'PUT' && request.url === '/viewport') {
  const viewport = validateViewport(await readJsonRequestBounded(request, 1024));
  await this.resizeViewport(viewport.width, viewport.height);
  response.writeHead(204);
  response.end();
  return;
}
```

Map malformed/out-of-range input to `400`, resize failures to `500`, preserve the existing `GET /healthz` behavior, and return `404` for every other route.

- [ ] **Step 8: Run all runtime tests**

Run:

```powershell
npm test
```

Expected: all browser-runtime tests PASS.

- [ ] **Step 9: Commit**

```powershell
git add browser-runtime/supervisor.mjs browser-runtime/test/supervisor.test.mjs
git commit -m "feat(browser): resize runtime viewport"
```

---

### Task 2: XRandR-capable browser image and VNC notifications

**Files:**
- Modify: `browser-runtime/Dockerfile`
- Modify: `browser-runtime/entrypoint.sh`
- Modify: `browser-runtime/test/entrypoint.test.mjs`

**Interfaces:**
- Consumes: `xrandr` binary invoked by `BrowserSupervisor`
- Produces: both x11vnc servers observe XRandR changes and emit `NewFBSize`

- [ ] **Step 1: Write failing entrypoint and image-source tests**

Extend `entrypoint.test.mjs` to record full stub arguments and assert that both x11vnc invocations contain:

```js
['-xrandr', 'resize']
```


- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
npm test -- --test-name-pattern "xrandr"
docker build -t open-agenthub-dev/browser:viewport-red browser-runtime
docker run --rm --entrypoint sh open-agenthub-dev/browser:viewport-red -lc "command -v xrandr"
```

Expected: the entrypoint test FAILS because the flags are absent, and the
container command exits non-zero because the old image has no `xrandr`.

- [ ] **Step 3: Install XRandR and enable resize tracking**

In `Dockerfile`, resolve and install `x11-xserver-utils` using the same pinned-candidate pattern as the existing Debian packages.

Change both x11vnc launches to include:

```sh
-xrandr resize
```

Keep writable port `5900` and view-only port `5901` otherwise unchanged.

- [ ] **Step 4: Run runtime tests**

Run:

```powershell
npm test
```

Expected: all browser-runtime tests PASS.

- [ ] **Step 5: Build and smoke-test the runtime image**

Run:

```powershell
docker build -t open-agenthub-dev/browser:viewport-test browser-runtime
docker run --rm --entrypoint sh open-agenthub-dev/browser:viewport-test -lc "command -v xrandr && x11vnc -opts 2>&1 | grep xrandr"
```

Expected: `/usr/bin/xrandr` and the x11vnc `-xrandr` option are reported.

- [ ] **Step 6: Commit**

```powershell
git add browser-runtime/Dockerfile browser-runtime/entrypoint.sh browser-runtime/test/entrypoint.test.mjs
git commit -m "feat(browser): enable dynamic XRandR sizing"
```

---

### Task 3: Backend runtime client and owner-only API

**Files:**
- Create: `backend/Browser/BrowserViewport.cs`
- Create: `backend/Browser/BrowserRuntimeClient.cs`
- Create: `backend/Controllers/BrowserViewportController.cs`
- Modify: `backend/Browser/IBrowserService.cs`
- Modify: `backend/Browser/KubernetesBrowserService.cs`
- Modify: `backend/Program.cs`
- Create: `tests/AgentHub.Api.Tests/BrowserRuntimeClientTests.cs`
- Create: `tests/AgentHub.Api.Tests/BrowserViewportControllerTests.cs`
- Modify: `tests/AgentHub.Api.Tests/KubernetesBrowserServiceTests.cs`
- Modify: `tests/AgentHub.Api.Tests/BrowserControllerTests.cs`
- Modify: `tests/AgentHub.Api.Tests/BrowserReconcileServiceTests.cs`

**Interfaces:**
- Produces: `BrowserViewport.TryCreate(int width, int height, out BrowserViewport? viewport)`
- Produces: `IBrowserRuntimeClient.ResizeAsync(string podIp, BrowserViewport viewport, CancellationToken ct)`
- Produces: `IBrowserService.ResizeAsync(string sessionId, BrowserViewport viewport, CancellationToken ct)`
- Produces: `PUT /api/sessions/{id}/browser/viewport`
- Consumes: active `BrowserLease` with `Phase == Running` and non-null `PodIp`

- [ ] **Step 1: Write failing viewport value-object tests**

Add xUnit theories proving inclusive limits, non-integer JSON rejection at model binding, and rejection outside:

```csharp
[InlineData(480, 320, true)]
[InlineData(2560, 1600, true)]
[InlineData(479, 320, false)]
[InlineData(2561, 1600, false)]
```

- [ ] **Step 2: Run the focused backend tests and verify RED**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter BrowserViewport
```

Expected: FAIL because the viewport types and controller do not exist.

- [ ] **Step 3: Implement the value object and runtime client**

Create:

```csharp
public sealed record BrowserViewport(int Width, int Height)
{
    public const int MinWidth = 480;
    public const int MinHeight = 320;
    public const int MaxWidth = 2560;
    public const int MaxHeight = 1600;
    public static bool TryCreate(int width, int height, out BrowserViewport? viewport);
}

public interface IBrowserRuntimeClient
{
    Task ResizeAsync(string podIp, BrowserViewport viewport, CancellationToken ct = default);
}
```

`BrowserRuntimeClient` must use an injected `HttpClient`, set a five-second request timeout with a linked cancellation token, and send JSON via `PUT http://{podIp}:6081/viewport`. Require a success status.

Register it with `AddHttpClient<IBrowserRuntimeClient, BrowserRuntimeClient>()`.

- [ ] **Step 4: Write failing service lease tests**

Add tests proving:

- a running lease calls the runtime client with its recorded pod IP;
- Pending, Stopping, Failed, missing, or null-IP leases throw `InvalidOperationException`;
- a runtime `HttpRequestException` is not converted into a browser stop or lease mutation.

Update every existing `IBrowserService` test fake with the new method so the solution compiles.

- [ ] **Step 5: Run service tests and verify RED**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter KubernetesBrowserServiceTests
```

Expected: FAIL because `ResizeAsync` is not implemented.

- [ ] **Step 6: Implement service forwarding**

Inject `IBrowserRuntimeClient` into `KubernetesBrowserService` and implement:

```csharp
public async Task ResizeAsync(string sessionId, BrowserViewport viewport, CancellationToken ct = default)
{
    var lease = await _leases.GetBySessionAsync(sessionId, ct);
    if (lease is not { Phase: BrowserPhase.Running, PodIp: not null })
        throw new InvalidOperationException("Browser is not running.");
    await _runtime.ResizeAsync(lease.PodIp, viewport, ct);
}
```

- [ ] **Step 7: Write failing owner-only controller tests**

Instantiate `BrowserViewportController` with a claims principal and fakes. Prove:

- the owner receives `204` and the exact viewport is forwarded;
- a different authenticated user receives `404`;
- invalid bounds receive `400`;
- a non-running browser maps to `409`;
- `HttpRequestException`, timeout, or runtime failure maps to `502`;
- the controller defines no shared-token route.

- [ ] **Step 8: Implement the controller**

Use `[ApiController]`, `[Authorize]`, and:

```csharp
[Route("api/sessions/{id}/browser/viewport")]
[HttpPut]
```

Resolve the owner claim exactly like `SessionsController`, load the session with `ISessionStore.GetAsync(owner, id, ct)`, validate `BrowserViewportRequest`, then call `IBrowserService.ResizeAsync`. Return `NoContent`, `BadRequest`, `NotFound`, `Conflict`, or `StatusCode(502)` as specified.

- [ ] **Step 9: Run all backend tests**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj
```

Expected: all backend tests PASS.

- [ ] **Step 10: Commit**

```powershell
git add backend/Browser backend/Controllers/BrowserViewportController.cs backend/Program.cs tests/AgentHub.Api.Tests
git commit -m "feat(browser): add owner viewport API"
```

---

### Task 4: Backend-only viewport NetworkPolicy

**Files:**
- Modify: `backend/Browser/BrowserPodSpecFactory.cs`
- Modify: `tests/AgentHub.Api.Tests/BrowserPodSpecFactoryTests.cs`

**Interfaces:**
- Produces: browser ingress to ports `6080`, `6081`, and `6082` from backend pods only
- Preserves: CDP remains accessible only from the exact session pod IP

- [ ] **Step 1: Change the policy test first**

Update `Build_VncPolicyAcceptsOnlyBackendNamespaceAndPods` to require:

```csharp
Assert.Equal(
    ["6080", "6081", "6082"],
    policy.Spec.Ingress.Single().Ports.Select(port => port.Port.Value).ToArray());
```

Also assert the only peer combines the control namespace selector and
`app=agenthub-backend` pod selector.

- [ ] **Step 2: Run the policy test and verify RED**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter Build_VncPolicyAcceptsOnlyBackendNamespaceAndPods
```

Expected: FAIL because port `6081` is absent.

- [ ] **Step 3: Permit the control port**

Add TCP port `6081` to the existing backend-only `VncIngress` rule. Do not create an unrestricted health-port rule.

- [ ] **Step 4: Run browser policy and backend suites**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter BrowserPodSpecFactoryTests
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add backend/Browser/BrowserPodSpecFactory.cs tests/AgentHub.Api.Tests/BrowserPodSpecFactoryTests.cs
git commit -m "fix(browser): restrict viewport control to backend"
```

---

### Task 5: Frontend measurement, debounce, and contain fallback

**Files:**
- Modify: `frontend/src/api.js`
- Modify: `frontend/src/components/BrowserPane.vue`
- Replace: `frontend/src/components/browser-cover.test.js`
- Test: `frontend/src/components/browser-connection-stability.test.js`
- Test: `frontend/src/components/browser-workspace.test.js`

**Interfaces:**
- Produces: `resizeBrowserViewport(id, width, height): Promise<void>`
- Consumes: `PUT /api/sessions/{id}/browser/viewport`
- Preserves: existing RFB instance across polling, access changes, and viewport changes

- [ ] **Step 1: Read the test-quality rules**

Read `superpowers/test-driven-development/writing-good-tests.md` completely before changing component tests.

- [ ] **Step 2: Replace cover expectations with failing owner-resize tests**

Rename the suite to `browser dynamic viewport`. Mock `resizeBrowserViewport` and use fake timers. Prove:

```js
setSize(host, 800, 700);
observer.callback();
await vi.advanceTimersByTimeAsync(249);
expect(resizeBrowserViewport).not.toHaveBeenCalled();
await vi.advanceTimersByTimeAsync(1);
expect(resizeBrowserViewport).toHaveBeenCalledWith('s1', 800, 700);
```

Add tests proving:

- rapid `800×700`, `810×700`, `820×700` changes submit only `820×700`;
- a duplicate size is not resubmitted;
- `sharedToken` or `canWrite: false` submits nothing;
- dimensions below `480×320` submit nothing;
- resize failures leave the RFB instance connected;
- unmount clears the observer and pending timer.

- [ ] **Step 3: Run component tests and verify RED**

Run:

```powershell
npm test -- src/components/browser-cover.test.js
```

Expected: FAIL because the component still applies cover scaling and has no viewport API call.

- [ ] **Step 4: Implement the frontend API**

Add:

```js
export const resizeBrowserViewport = (id, width, height) =>
  req('PUT', `/sessions/${encodeURIComponent(id)}/browser/viewport`, { width, height })
```

- [ ] **Step 5: Implement measured owner resizing**

In `BrowserPane.vue`:

- remove `applyCoverScale`;
- set `connection.scaleViewport = true`;
- keep `connection.resizeSession = false`;
- track latest measurement, last submitted dimensions, and a 250 ms timer;
- only submit for Running, writable, non-shared sessions within bounds;
- call the same queue from `ResizeObserver`, RFB `connect`, and a transition to writable access;
- catch resize request errors without changing `connectionState`;
- clear the timer and observer on unmount.

Remove the CSS that centers and forcibly crops the noVNC canvas. Keep the host `overflow: hidden` so contain scaling cannot introduce scrollbars.

- [ ] **Step 6: Run focused frontend tests**

Run:

```powershell
npm test -- src/components/browser-cover.test.js src/components/browser-connection-stability.test.js src/components/browser-workspace.test.js
```

Expected: all focused tests PASS and instance-count assertions remain unchanged.

- [ ] **Step 7: Run the full frontend suite and build**

Run:

```powershell
npm test
npm run build
```

Expected: all tests PASS and Vite build exits `0`.

- [ ] **Step 8: Commit**

```powershell
git add frontend/src/api.js frontend/src/components/BrowserPane.vue frontend/src/components/browser-cover.test.js frontend/src/components/browser-connection-stability.test.js frontend/src/components/browser-workspace.test.js
git commit -m "fix(browser): match remote viewport to pane"
```

---

### Task 6: Full verification and local Kubernetes rollout

**Files:**
- Verify only unless test findings require a focused fix

**Interfaces:**
- Verifies the complete frontend → backend → browser-runtime → Xvfb → x11vnc path

- [ ] **Step 1: Run fresh complete automated verification**

Run:

```powershell
npm test
```

from `browser-runtime`.

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj
```

from the repository root.

Run:

```powershell
npm test
npm run build
```

from `frontend`.

Expected: every command exits `0` with zero test failures.

- [ ] **Step 2: Run source and whitespace checks**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; only intentional uncommitted changes, if any.

- [ ] **Step 3: Deploy the current worktree**

Run:

```powershell
./setup-dev.ps1 -NoPortForward
```

Expected: Helm upgrade succeeds and backend/frontend rollouts complete.

- [ ] **Step 4: Start or refresh the local frontend forward**

Ensure exactly one hidden process runs:

```powershell
kubectl -n agenthub-dev port-forward svc/agenthub-frontend 8080:80
```

Expected: `http://127.0.0.1:8080/` returns HTTP `200`.

- [ ] **Step 5: Verify dynamic runtime behavior**

Open the existing local Browser session and change the split width and window height. For at least three pane sizes:

1. inspect the `.browser-canvas` `clientWidth` and `clientHeight`;
2. run `DISPLAY=:99 xdpyinfo` in the matching browser pod;
3. confirm X dimensions match the pane within integer rounding;
4. confirm the whole Chromium window is visible;
5. click near all four corners and verify accurate interaction;
6. confirm the RFB WebSocket does not reconnect.

Open a shared view, resize it, and confirm `xdpyinfo` dimensions do not change.

- [ ] **Step 6: Verify cluster security resources**

Run:

```powershell
kubectl -n agenthub-dev-sessions get networkpolicy -o yaml
kubectl -n agenthub-dev get deployments,pods
```

Expected: browser control port `6081` is admitted only from backend pods; all control workloads are Ready.

- [ ] **Step 7: Commit any verification-only test adjustments**

If no files changed, skip this step. Otherwise:

```powershell
git add browser-runtime/test/supervisor.test.mjs browser-runtime/test/entrypoint.test.mjs tests/AgentHub.Api.Tests/BrowserRuntimeClientTests.cs tests/AgentHub.Api.Tests/BrowserViewportControllerTests.cs tests/AgentHub.Api.Tests/KubernetesBrowserServiceTests.cs tests/AgentHub.Api.Tests/BrowserPodSpecFactoryTests.cs frontend/src/components/browser-cover.test.js frontend/src/components/browser-connection-stability.test.js frontend/src/components/browser-workspace.test.js
git commit -m "test(browser): cover dynamic viewport integration"
```

- [ ] **Step 8: Push the verified branch**

Run:

```powershell
git push origin feat/integrated-browser
```

Expected: remote branch advances to the final verified commit.
