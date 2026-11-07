// A WebSocket's opening handshake has no timeout of its own: the browser waits as long as the
// path holds the request. And Chrome lets only one WebSocket per host and port be in CONNECTING,
// queueing every other one behind it (RFC 6455, 4.1). So a single upgrade that an ingress parked
// on a starved backend replica stalled every socket of the page: the Workspace's retry after a
// slow pod start was queued behind it, never opened and never closed, and since the panes only
// retry from onclose, the Workspace said "disconnected" until a reload (#520).
//
// Closing a socket that is still CONNECTING fails it, which fires the same close event as any
// other failed attempt, so the caller's ordinary retry path takes over and the queue moves on.
// Twice what the backend may spend before it accepts (it confirms the pod with the Kubernetes
// API, bounded at 5 s in AgentPodRequests), and short enough that an attempt lost to a held
// upgrade costs seconds, not "until reload".
export const HANDSHAKE_TIMEOUT_MS = 10_000

// The numeric state, not WebSocket.CONNECTING: the global can be a stand-in without the
// constants, where the comparison would match every socket.
const CONNECTING = 0

export function limitHandshake(socket, timeoutMs = HANDSHAKE_TIMEOUT_MS) {
  const timer = setTimeout(() => {
    if (socket.readyState === CONNECTING) {
      try { socket.close() } catch { /* already gone */ }
    }
  }, timeoutMs)
  const clear = () => clearTimeout(timer)
  socket.addEventListener?.('open', clear)
  socket.addEventListener?.('close', clear)
  return clear
}
