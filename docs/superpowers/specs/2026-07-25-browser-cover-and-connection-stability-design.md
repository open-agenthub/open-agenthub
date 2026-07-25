# Browser Cover Scaling and Connection Stability

## Problem

The embedded browser uses noVNC `scaleViewport`, which preserves the remote
framebuffer aspect ratio by fitting it entirely inside the available pane.
When the browser pane is taller or narrower than the 1440×900 framebuffer,
large letterbox areas remain visible.

Session state is also refreshed periodically. `BrowserPane` watches a getter
that returns a new array containing the session id, browser phase, and share
token. Replacing the session object causes that getter to produce a new array
reference even when all watched values are unchanged. The watcher therefore
disconnects and recreates the noVNC client on every refresh.

## Considered approaches

1. Enable noVNC `resizeSession`. This uses the public API and would avoid
   cropping, but depends on ExtendedDesktopSize support from Xvfb and x11vnc.
   The current runtime cannot rely on that capability.
2. Apply a proportional cover scale through noVNC's display scale and center
   the resulting canvas in an overflow-hidden screen. This fills the pane,
   preserves pointer coordinate mapping, and is the selected approach.
3. Apply CSS `object-fit: cover` directly to the canvas. This is visually
   simple but noVNC would retain a different internal scale for pointer
   coordinates, so clicks could target the wrong remote location.

## Design

`BrowserPane` keeps noVNC `scaleViewport` disabled and owns the cover
calculation. The scale is:

```text
max(host width / framebuffer width, host height / framebuffer height)
```

The calculation uses the connected RFB display dimensions and updates its
display scale so rendering and input coordinates share the same factor. The
noVNC screen remains the full size of the pane, hides overflow, and centers
the canvas in both axes. The result fills the pane without distortion and
crops equal portions from opposite edges when aspect ratios differ.

A `ResizeObserver` watches the browser host. It reapplies cover scaling after
the connection opens and whenever the window, mobile pane, or split position
changes. The observer is disconnected when the component unmounts.

## Connection lifecycle

The connection watcher observes stable primitive sources individually:
session id, browser phase, and share token. Replacing a polled session object
with another object containing the same values does not reconnect.

A real change to session id, browser phase, or share token closes the old RFB
instance and starts the appropriate new lifecycle. Changing `canWrite` updates
the live RFB instance's `viewOnly` property without disconnecting, because the
WebSocket route and browser identity do not change.

Unexpected RFB disconnects retain the existing bounded exponential reconnect
behavior. Component unmount still cancels pending reconnects and disconnects
the active client.

## Testing

Frontend component tests cover:

- replacing the session object with the same id and running phase does not
  create another RFB instance;
- changing session id, phase, or share token does reconnect;
- changing `canWrite` updates `viewOnly` without reconnecting;
- cover scale uses the larger width/height ratio;
- the resize observer reapplies cover scaling and is disconnected on unmount;
- viewer input remains disabled.

The full frontend test suite and production build must pass. The updated
frontend image is then deployed to the local Kubernetes release for visual
verification at multiple split widths.

