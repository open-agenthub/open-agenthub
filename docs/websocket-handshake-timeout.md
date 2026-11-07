# Bounding the WebSocket handshake (#520)

## What went wrong

After a slow session pod start in the emulated-VM demo rig (two backend replicas behind the k3s
ingress, load 30 on 4 vCPUs), the Workspace stayed "disconnected" after the pod was ready, until
the page was reloaded.

The terminal pane, the chat stream and the session event socket all retry from `onclose`. A
browser WebSocket has no handshake timeout, and Chrome allows only one WebSocket per host and port
in CONNECTING, queueing the rest (RFC 6455, 4.1). When the ingress handed one upgrade to a replica
that took it and did not answer, that socket never opened and never closed. Every later socket of
the page queued behind it, the terminal's retry included, so no `onclose` ever came and the pane
never tried again. A reload drops the held request and starts over.

`tests/WorkspaceReconnectProof` reproduces this against the real backend and kube-apiserver with a
two-replica ingress stand-in: on main the page creates no terminal socket after the pod is ready.
`isLive` stays true throughout; the reconnect logic itself is not at fault.

## Decision

Each of the three client sockets closes itself if its handshake has not completed after 10 s
(`frontend/src/lib/handshake.js`). Closing a CONNECTING socket fails it with the same close event
as any other failed attempt, so the existing retry paths take over unchanged, and the queue behind
it moves on. 10 s is twice the bound on what the backend does before it accepts (the 5 s pod
confirmation in `AgentPodRequests`).

## Where the backend holds an upgrade

Of the sockets the Workspace opens, `/events` does nothing before it accepts except an access
lookup in Postgres; with the Kubernetes API frozen it still opens at once. The terminal and shell
upgrades first read the session (pod phase and IP) from the Kubernetes API, and that client's own
timeout is 100 s: with the API server frozen, the real backend held the terminal handshake for
100.0 s and then answered 500. Under the browser's queue that is 100 s in which no other socket of
the page can open.

The backend now bounds that read for an upgrade at 3 s (`TerminalProxy.SessionReadTimeout`) and
answers 503 with `Retry-After: 2`; measured against the frozen API server, 503 after 3.0 s. With
the 5 s pod confirmation that stays under the client's 10 s. It does not by itself shorten the
recovery after a stall: the attempt in flight when the API server comes back completes at once,
with or without the bound (both connected within a second of the thaw in the proof). It keeps the
backend from being the thing that blocks the page's other sockets, and it says "retry" instead of
holding the request.

The client timeout stays the primary fix, because it also covers holds the backend never sees
(an ingress, a starved replica, a lost connection).

## Alternatives not taken

- **A response timeout on the ingress.** It would fix only installations whose ingress we
  configure, and the chart supports several ingress controllers. The browser is the one place that
  sees every path.
- **Backend: connect to the pod first, accept the browser afterwards.** Today the backend accepts
  the browser's socket before it opens the upstream connection, so "connected" shows briefly while
  the agent is still starting. That is cosmetic: the socket then closes and the client retries,
  as the proof's runs without the held replica show. It cannot help with a request that never
  reaches a working replica, which is the failure here.
- **A heartbeat on open sockets.** It detects a dead open connection, not a handshake that never
  completes.
