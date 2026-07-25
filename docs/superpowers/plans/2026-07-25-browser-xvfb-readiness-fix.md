# Browser Xvfb Readiness Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the non-root browser runtime wait deterministically for Xvfb before starting Chromium and VNC.

**Architecture:** The existing shell entrypoint remains the process supervisor. A bounded readiness gate prepares a configurable X11 socket directory, starts Xvfb, verifies that Xvfb remains alive, and releases all dependent processes only after the display socket exists. A Node regression test executes the real entrypoint with stub processes inside the browser image.

**Tech Stack:** POSIX shell, Node.js built-in test runner, Docker, PowerShell, Kubernetes, Helm

## Global Constraints

- Keep the browser container at UID/GID 1000 with a read-only root filesystem.
- Do not add a privileged or root init container.
- Do not weaken existing Kubernetes NetworkPolicies.
- Preserve existing process supervision and graceful shutdown behavior.
- The readiness wait must be bounded and emit a clear failure diagnostic.

---

### Task 1: Xvfb readiness gate

**Files:**
- Create: `browser-runtime/test/entrypoint.test.mjs`
- Modify: `browser-runtime/entrypoint.sh`

**Interfaces:**
- Consumes: `AGENTHUB_BROWSER_X11_SOCKET_DIR`, defaulting to `/tmp/.X11-unix`
- Consumes: `AGENTHUB_BROWSER_X11_READY_ATTEMPTS`, defaulting to `100`
- Produces: dependent browser processes start only after `$AGENTHUB_BROWSER_X11_SOCKET_DIR/X99` exists

- [ ] **Step 1: Write the failing entrypoint regression test**

Create `browser-runtime/test/entrypoint.test.mjs`. The test must use
`node:test`, `node:assert/strict`, `node:child_process`, `node:fs/promises`,
`node:os`, and `node:path`. It must:

1. Skip when `/bin/sh` is unavailable.
2. Create a temporary `bin` directory and event log.
3. Create an `Xvfb` stub that verifies the socket directory already exists,
   waits 250 ms, creates `X99`, records `Xvfb-ready`, and remains alive.
4. Create `chromium`, `x11vnc`, `websockify`, `socat`, and `node` stubs that
   record `<name>-early` and exit if `X99` is absent; otherwise record the
   executable name and remain alive.
5. Spawn `/bin/sh entrypoint.sh` with the stub directory first in `PATH`,
   `AGENTHUB_BROWSER_X11_SOCKET_DIR` set to the temporary socket directory,
   and `AGENTHUB_BROWSER_X11_READY_ATTEMPTS=50`.
6. Wait up to five seconds for the `node` event, terminate the entrypoint, and
   assert that `Xvfb-ready` precedes every dependent-process event and no event
   ends in `-early`.
7. Remove the temporary directory in `finally`.

The stubs must trap `TERM` and `INT`, so entrypoint shutdown completes without
waiting for the 47-second supervisor deadline.

- [ ] **Step 2: Run the regression test and verify RED**

Run from `browser-runtime`:

```powershell
docker run --rm --entrypoint node `
  -v "${PWD}:/work:ro" -w /work `
  open-agenthub-dev/browser:local `
  --test test/entrypoint.test.mjs
```

Expected: FAIL because the current entrypoint starts at least one dependent
stub before `X99` exists.

- [ ] **Step 3: Implement the minimal readiness gate**

Update `browser-runtime/entrypoint.sh` immediately before and after the
existing Xvfb start:

```sh
X11_SOCKET_DIR="${AGENTHUB_BROWSER_X11_SOCKET_DIR:-/tmp/.X11-unix}"
X11_READY_ATTEMPTS="${AGENTHUB_BROWSER_X11_READY_ATTEMPTS:-100}"

case "$X11_READY_ATTEMPTS" in
  ''|*[!0-9]*|0) X11_READY_ATTEMPTS=100 ;;
esac

mkdir -p /data/chromium /data/home /tmp/runtime "$X11_SOCKET_DIR"

start_child Xvfb :99 -screen 0 "${SCREEN}x24" -nolisten tcp
XVFB_PID=$CHILD_PID
attempt=0
while [ ! -e "$X11_SOCKET_DIR/X99" ]; do
  if ! kill -0 "$XVFB_PID" 2>/dev/null; then
    wait "$XVFB_PID" || status=$?
    echo "[browser-runtime] Xvfb exited before display :99 became ready: status=${status:-0}" >&2
    exit "${status:-1}"
  fi
  if [ "$attempt" -ge "$X11_READY_ATTEMPTS" ]; then
    echo "[browser-runtime] display :99 did not become ready" >&2
    exit 1
  fi
  attempt=$((attempt + 1))
  sleep 0.1
done
```

Keep all dependent `start_child` calls after this block.

- [ ] **Step 4: Verify GREEN and run runtime tests**

Run the same Docker test command from Step 2.

Expected: PASS, with no `-early` event.

Then run:

```powershell
npm test
./smoke.ps1 -Build
```

Expected: all Node tests pass and `Browser runtime smoke test passed.`

- [ ] **Step 5: Commit the tested runtime fix**

```powershell
git add browser-runtime/test/entrypoint.test.mjs browser-runtime/entrypoint.sh
git commit -m "fix(browser): wait for Xvfb readiness"
```

### Task 2: Local Kubernetes acceptance

**Files:**
- Verify: `setup-dev.ps1`
- Verify: `tests/browser-smoke.ps1`

**Interfaces:**
- Consumes: local Docker Desktop Kubernetes context
- Produces: a deployed browser image that passes the end-to-end browser acceptance test

- [ ] **Step 1: Build and deploy all current feature images**

Run from the repository worktree root:

```powershell
./setup-dev.ps1 -NoPortForward
```

Expected: Helm upgrade succeeds, backend and frontend rollouts complete, and
the health checks report that the development release is ready.

- [ ] **Step 2: Run the full Kubernetes browser smoke test**

```powershell
./tests/browser-smoke.ps1
```

Expected:

- matching session agent lifecycle request returns 200;
- unauthenticated foreign pod receives 401;
- foreign pod with a stolen callback token receives 401;
- matching agent reaches CDP;
- foreign pod cannot reach CDP;
- browser viewer WebSocket opens;
- S3 cookie restore passes when S3 credentials are configured, otherwise that
  check is explicitly skipped;
- temporary session, browser, service, and NetworkPolicy resources are removed.

- [ ] **Step 3: Verify cluster and worktree state**

```powershell
kubectl -n agenthub-dev get deployments,pods
kubectl -n agenthub-dev-sessions get pods,services,networkpolicies
git status --short
```

Expected: control-plane workloads are Ready, no resources from the new smoke
session remain, and the worktree is clean. Pre-existing failed session pods
must not be deleted as part of this task.

- [ ] **Step 4: Push the verified branch**

```powershell
git push origin feat/integrated-browser
```

Expected: `origin/feat/integrated-browser` advances to the tested fix commit.

