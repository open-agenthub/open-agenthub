# Browser Xvfb Readiness Fix

## Problem

The browser runtime starts Xvfb, Chromium, both x11vnc processes, websockify,
socat, and the Node supervisor without waiting for the X display to become
available. In Kubernetes, `/tmp` is an emptyDir mounted into the non-root
container. The first x11vnc process can therefore run before the X11 socket
directory and display socket exist. It exits with status 1, the entrypoint
treats that as a required-process failure, and the browser never becomes
ready.

## Considered approaches

1. Add a fixed sleep after starting Xvfb. This is small but timing-dependent
   and would remain flaky on slow or heavily loaded nodes.
2. Prepare the X11 socket directory and poll for the display socket while also
   checking that Xvfb remains alive. This is deterministic, keeps the existing
   non-root container model, and is the selected approach.
3. Add a privileged or root init container to prepare `/tmp`. This adds
   Kubernetes complexity and weakens the current security posture for a
   problem that the runtime can solve itself.

## Design

The entrypoint creates `/tmp/.X11-unix` before starting Xvfb. After Xvfb is
spawned, a bounded readiness loop waits for `/tmp/.X11-unix/X99`. The loop
fails immediately if the Xvfb process exits and fails with a clear diagnostic
if the socket does not appear before the deadline.

Chromium, x11vnc, websockify, socat, and the Node supervisor start only after
the display socket exists. Process supervision, shutdown ordering, container
UID, read-only root filesystem, and Kubernetes security settings remain
unchanged.

## Regression testing

An entrypoint test runs with stub executables and an initially missing X11
socket directory. The Xvfb stub creates the display socket after a short delay.
The test verifies that neither x11vnc nor Chromium starts before the socket is
present. This test must fail against the current entrypoint before the fix is
applied.

The existing browser-runtime unit tests and container smoke test then run.
Finally, the feature images are rebuilt and deployed to the local Kubernetes
cluster, followed by the full `tests/browser-smoke.ps1` acceptance test. That
test must cover the authorized lifecycle request, rejection of foreign callers,
CDP isolation, viewer WebSocket access, cleanup, and cookie restoration when S3
is configured.

