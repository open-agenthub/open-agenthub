# Browser Cover Scaling and Connection Stability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill the embedded browser pane without letterboxing and stop unchanged session polling from recreating the noVNC connection.

**Architecture:** `BrowserPane` retains ownership of the RFB lifecycle and adds a small cover-scaling function that updates noVNC's display scale from host and framebuffer dimensions. Vue watches stable primitive sources instead of a newly allocated array, while a `ResizeObserver` reapplies the scale when the pane changes size.

**Tech Stack:** Vue 3 Composition API, noVNC 1.7.0, Vitest, Vue Test Utils, happy-dom, CSS

## Global Constraints

- Preserve framebuffer aspect ratio and center the crop.
- Keep rendering scale and noVNC pointer-coordinate scale identical.
- Do not reconnect when polling replaces the session object with unchanged identity and phase.
- Reconnect when session id, browser phase, or share token changes.
- Update `viewOnly` in place when `canWrite` changes.
- Preserve existing exponential reconnect and unmount cleanup behavior.
- Do not change the browser runtime, VNC protocol, or authorization model.

---

### Task 1: Stable RFB lifecycle

**Files:**
- Modify: `frontend/src/components/browser-workspace.test.js`
- Modify: `frontend/src/components/BrowserPane.vue`

**Interfaces:**
- Consumes: `session.id`, `session.browser.phase`, `sharedToken`, and `canWrite`
- Produces: one live RFB instance while connection identity is unchanged

- [ ] **Step 1: Add failing lifecycle regression tests**

Extend the noVNC test double with `_display` dimensions and add component tests
that mount a running browser and assert:

```js
await wrapper.setProps({
  session: { id: 's1', title: 'refreshed', browser: { phase: 'Running' } }
})
await flushPromises()
expect(novnc.instances).toHaveLength(1)
```

Then update write access and assert the existing instance changes in place:

```js
await wrapper.setProps({ canWrite: false })
await flushPromises()
expect(novnc.instances).toHaveLength(1)
expect(novnc.instances[0].viewOnly).toBe(true)
```

Add one control assertion that changing `session.id` creates a second RFB
instance and disconnects the first.

- [ ] **Step 2: Run lifecycle tests and verify RED**

Run:

```powershell
npm test -- src/components/browser-workspace.test.js
```

from `frontend`.

Expected: the unchanged-session and in-place `canWrite` assertions fail because
the current watchers call `syncConnection`.

- [ ] **Step 3: Implement stable watchers**

Replace the array-returning watcher with individual sources:

```js
watch([
  () => props.session.id,
  phase,
  () => props.sharedToken
], syncConnection)
```

Replace the `canWrite` reconnect watcher with:

```js
watch(() => props.canWrite, canWrite => {
  if (rfb) rfb.viewOnly = !canWrite
})
```

- [ ] **Step 4: Run lifecycle tests and verify GREEN**

Run the same targeted Vitest command.

Expected: all browser workspace tests pass and unchanged polling retains one
RFB instance.

### Task 2: Proportional cover scaling

**Files:**
- Modify: `frontend/src/components/browser-workspace.test.js`
- Modify: `frontend/src/components/BrowserPane.vue`

**Interfaces:**
- Consumes: host `clientWidth`/`clientHeight` and RFB `_display.width`/`height`
- Produces: `display.scale = max(hostWidth / framebufferWidth, hostHeight / framebufferHeight)`

- [ ] **Step 1: Add failing cover and observer tests**

Add `ResizeObserver` test support that records the callback, observed element,
and `disconnect` call. Configure the RFB test double with:

```js
this._display = { width: 1440, height: 900, scale: 1 }
```

For an 800×800 host, dispatch the RFB `connect` event and assert:

```js
expect(instance.scaleViewport).toBe(false)
expect(instance._display.scale).toBeCloseTo(800 / 900)
```

Change the host to 1200×600, invoke the observer callback, and assert:

```js
expect(instance._display.scale).toBeCloseTo(1200 / 1440)
```

Unmount and assert that the observer disconnected.

- [ ] **Step 2: Run cover tests and verify RED**

Run:

```powershell
npm test -- src/components/browser-workspace.test.js
```

Expected: FAIL because `scaleViewport` is currently true, no cover scale is
written, and no resize observer exists.

- [ ] **Step 3: Implement cover scaling**

Add component state:

```js
let resizeObserver
```

Add a bounded cover function:

```js
function applyCoverScale(connection = rfb) {
  const display = connection?._display
  const element = host.value
  if (!display || !element || display.width <= 0 || display.height <= 0 ||
      element.clientWidth <= 0 || element.clientHeight <= 0) return
  display.scale = Math.max(
    element.clientWidth / display.width,
    element.clientHeight / display.height
  )
}
```

Set `connection.scaleViewport = false`, call `applyCoverScale(connection)` in
the RFB `connect` listener, and create the observer during mount:

```js
resizeObserver = new ResizeObserver(() => applyCoverScale())
resizeObserver.observe(host.value)
syncConnection()
```

Disconnect it during unmount.

Center and crop the noVNC DOM while keeping the existing visual system:

```css
.browser-canvas :deep(> div) {
  align-items: center;
  justify-content: center;
  overflow: hidden !important;
}
.browser-canvas :deep(canvas) {
  flex: none;
  margin: 0 !important;
  outline: none;
}
```

- [ ] **Step 4: Run targeted and full frontend verification**

Run:

```powershell
npm test -- src/components/browser-workspace.test.js
npm test
npm run build
```

Expected: targeted tests pass, the full suite has zero failures, and Vite
produces a successful production build.

- [ ] **Step 5: Commit the frontend fix**

```powershell
git add frontend/src/components/BrowserPane.vue frontend/src/components/browser-workspace.test.js
git commit -m "fix(browser): cover pane without reconnect flicker"
```

### Task 3: Local Kubernetes visual acceptance

**Files:**
- Verify: `setup-dev.ps1`
- Verify: `frontend/src/components/BrowserPane.vue`

**Interfaces:**
- Consumes: local Docker Desktop development release
- Produces: updated frontend image in the existing `agenthub-dev` Helm release

- [ ] **Step 1: Rebuild and deploy**

Run:

```powershell
./setup-dev.ps1 -NoPortForward
```

Expected: Helm upgrade succeeds and frontend/backend rollouts become Ready.

- [ ] **Step 2: Verify cluster and worktree state**

Run:

```powershell
kubectl -n agenthub-dev get deployments,pods
git status --short
```

Expected: development workloads are Ready and the worktree is clean.

- [ ] **Step 3: Push the verified branch**

Run:

```powershell
git push origin feat/integrated-browser
```

Expected: the remote branch advances through the cover-scaling fix and all
previously verified browser hardening commits.
