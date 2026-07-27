# Dynamic Browser Viewport Design

## Goal

The integrated browser must use the exact pixel width and height of the browser
pane in the owning AgentHub session view. The remote Chromium desktop is resized
instead of cropping or distorting a fixed framebuffer. The result has no
scrollbars, no hidden browser regions, and correct pointer coordinates.

## Ownership Rule

Only a normal authenticated session view with write access may request a remote
viewport resize. Shared-link views never change the browser pod resolution,
including shared-control links. They display the current remote framebuffer with
proportional contain scaling.

This prevents different shared viewers from repeatedly changing a single
browser desktop. Multiple owner tabs may each submit their measured size, but
each tab only sends an update after its own pane changes, so stationary tabs do
not create a resize loop.

## Chosen Architecture

The resize flows through an authenticated backend endpoint and a backend-only
browser-pod control endpoint:

1. `BrowserPane` measures the usable canvas area below its header with a
   `ResizeObserver`.
2. For a connected, writable, non-shared session, the frontend debounces the
   measurement and sends the requested width and height to the backend.
3. The backend resolves normal session access, rejects shared-token traffic,
   validates the requested dimensions, and looks up the active browser lease.
4. The backend calls the browser supervisor at the lease pod IP on the internal
   control port.
5. The supervisor serializes viewport changes, resizes Xvfb with XRandR, and
   updates the Chromium window bounds through CDP.
6. x11vnc observes the XRandR event and sends a new framebuffer size to noVNC.
7. The existing noVNC connection remains open and renders the updated desktop.

The VNC client-driven `resizeSession` extension is not used. Its availability
depends on server support and does not provide the same authorization,
validation, or error handling as the explicit AgentHub control path.

## Frontend Behavior

`BrowserPane` keeps one `ResizeObserver` for its canvas host. Measurements are
rounded to integer CSS pixels and sent only when all conditions are true:

- the browser phase is `Running`;
- no shared token is present;
- the current user has write access;
- width and height are non-zero;
- the measured dimensions differ from the last submitted dimensions.

Updates are debounced by 250 milliseconds. A newly connected browser receives
the latest measured size immediately after the debounce window. The frontend
does not reconnect noVNC when a resize is requested or completed.

While the remote framebuffer is catching up, noVNC uses proportional contain
scaling. The previous cover scaling and centered crop are removed. Shared views
always use this contain behavior.

Resize request failures do not hide or disconnect the browser. The last working
framebuffer remains visible, and a later pane resize may retry.

## Backend API and Authorization

The backend exposes a normal authenticated session endpoint:

`PUT /api/sessions/{sessionId}/browser/viewport`

Request body:

```json
{
  "width": 1024,
  "height": 768
}
```

The endpoint uses the existing session access service and requires write access
to the session. There is no equivalent shared-token route. A missing session,
insufficient access, non-running browser, or lease mismatch is rejected without
contacting a pod.

Both dimensions must be integers within these inclusive limits:

- minimum: `480 × 320`;
- maximum: `2560 × 1600`.

The desktop split layout keeps an interactive browser pane at or above the
minimum. If a smaller viewport occurs during a responsive layout transition,
the frontend does not submit an invalid size and temporarily uses proportional
contain scaling until the pane returns to the supported range.

The service forwards the validated dimensions only to the pod IP recorded by
the current running browser lease. Backend requests have a short timeout and do
not retry indefinitely.

## Browser Runtime

The browser image includes the XRandR command-line tool. Xvfb continues to start
with the configured default screen size. Both x11vnc processes start in XRandR
resize mode so writable and view-only VNC clients receive framebuffer changes.

The existing supervisor HTTP server on port `6081` adds:

`PUT /viewport`

The endpoint accepts only bounded JSON, validates the same dimension limits,
and applies viewport changes sequentially. If several requests arrive while a
resize is running, the supervisor may discard superseded intermediate sizes and
apply the latest requested size next.

For each resize, the supervisor:

1. invokes XRandR for display `:99`;
2. confirms the resulting X display dimensions;
3. obtains Chromium's top-level window through CDP;
4. sets its bounds to the new width and height.

The endpoint reports success only after both the X display and Chromium window
have been updated. A failed change returns an error and leaves the browser
process running.

## Network Isolation

Port `6081` remains an internal browser control and health port. The browser
pod ingress NetworkPolicy permits it only from backend pods in the control
namespace. Agent session pods, other browser pods, shared viewers, and external
clients cannot reach it.

The browser pod does not accept a session ID from callers and cannot select
another lease. The backend determines the target exclusively from the
authorized session and its active lease.

## Failure Handling

- Invalid or out-of-range dimensions return `400`.
- Missing write access returns the existing access-denied response.
- A browser that is not running returns `409`.
- Pod timeout or runtime failure returns `502`.
- Frontend failures are non-fatal and preserve the current framebuffer.
- x11vnc and Chromium remain supervised; a resize failure does not restart the
  pod or browser session.

Backend and supervisor logs record the session or lease identifier and requested
dimensions without recording credentials.

## Testing

Frontend component tests prove:

- owner views debounce and submit the measured canvas dimensions;
- shared views and read-only views never submit viewport changes;
- duplicate dimensions are suppressed;
- resize requests do not recreate the RFB instance;
- the fallback uses proportional contain scaling without crop or scrollbars.

Backend tests prove:

- write access is required;
- shared-token access has no resize route;
- bounds and integer validation;
- only the active running lease pod is contacted;
- runtime timeout and error mapping;
- the NetworkPolicy restricts port `6081` to backend pods.

Browser runtime tests prove:

- request body and dimension limits;
- sequential/latest-wins resize behavior;
- XRandR runs before Chromium bounds are changed;
- command or CDP failures return an error without stopping supervision;
- both x11vnc processes enable XRandR resize handling.

Local Kubernetes verification creates or reuses a browser session, resizes the
AgentHub split pane across different aspect ratios, and confirms that:

- X display dimensions match the browser canvas dimensions;
- the complete browser is visible without scrollbars or distortion;
- pointer interaction remains accurate;
- the VNC WebSocket remains connected;
- shared views cannot change the pod resolution.
