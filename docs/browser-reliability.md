# One browser window, an MCP that outlives a failed action, a lease that follows its pod

## One window

The viewer's VNC picture shows the whole X display, and there is no window manager in it, so a
second top-level Chromium window is drawn over the first. Once a second window exists, Chromium
puts every new tab, the agent's included, into whichever window last had focus. Chromium has no
switch or policy that opens popups as tabs.

Pages open windows through `window.open` with features (size, position, `popup`). The supervisor
adds an init script to the default context that keeps only `noopener` and `noreferrer` and drops
every other feature, which makes Chromium open a tab. The opener relationship survives, so a
sign-in popup can still `postMessage` its result back, and the returned reference still reports
`closed`. The script runs in every frame before the page's own scripts. A page can still detect
it, for example by checking whether `window.open` is a proxy.

What still opens a window, such as a shift-click, a CDP `Target.createTarget` with `newWindow`, or a
`window.open` the script never saw, is reopened as a tab of the first window and its own window
closed. The tab loads the URL a second time, as a GET: a form POST's result and the opener are
lost. The alternative was to leave the window, which keeps the problem this fixes. A window in
another browser context (an agent's own `browser.newContext()` over CDP) is left alone, because
it cannot be a tab of the default context. The in-pod browser MCP no longer creates a context of
its own.

## A failed action does not end the browser MCP

The browser MCP is a stdio server inside the agent's pod. When its process ends, the agent CLI
reports every browser tool as "Transport closed" until the session restarts. In Node, an error
that nothing awaits ends the process. The server now logs such an error to stderr (stdout is the
MCP stream), fails the calls in flight and drops its browser connection, so the next call
connects again. The reported transport close was not reproduced with a real browser. This guard
covers the mechanism, and the session-agent test shows the process surviving.

The connection itself is re-established by evidence, not by Playwright's `isConnected()`. A
browser pod that disappears without closing its sockets leaves the WebSocket open with nobody
behind it, and Playwright keeps calling it connected. After a failed action the MCP sends
`Browser.getVersion` with a five-second budget. If that does not answer, the connection is
dropped and the call reports `browser_connection_lost`. `browser_start` reconnects whenever the
backend hands out a different endpoint.

The agent's page is brought to the front before every action. Chromium stops animation frames
in a background tab, and Playwright's check that an element holds still waits on them, so a click
on a background tab timed out every time. A new tab, a popup or the viewer switching tabs put
the agent's page there. The cost is that the viewer's tab switches to the one the agent is
working in, which is also how the viewer sees what the agent does.

## The lease follows its pod

A browser pod runs with `RestartPolicy: Never`. When its browser exits or the pod is evicted, the
pod object stays behind in phase `Failed` or `Succeeded` with no failure code, and its address
answers nothing. The lease used to stay `Running` until someone stopped the browser by hand:
status read only the database, `browser_start` returned the recorded address, and reconcile
handled only pending startups.

The cluster client now reports a pod that has ended or is being deleted as gone. `browser_start`
checks the pod behind a Running lease and replaces it when it is gone, missing or labelled for
another lease. A pod that is alive but not ready is waited for, as a starting pod is. Single-session
status marks such a lease `Failed` with `pod_gone`, and the five-minute reconcile does the same
for the session list, which reads leases in bulk and does not ask the API server per session.
If the API server cannot be reached, a status check keeps the recorded lease, because the session
view and the browser proxy read the same status and should not fail with it.
