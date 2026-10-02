// A one-way event socket that reconnects on its own.
//
// The backend pushes {"type":"files"} when something about a session changes, and the caller
// answers by re-reading the REST endpoint it would otherwise poll. Nothing is read off the
// socket beyond the kind, so a message that does not parse is dropped rather than retried.
//
// The backoff matches the browser pane's (BrowserPane.vue): a reconnect storm against a backend
// that is rolling over is worse than a pane that takes a few seconds to come back.
const BASE_BACKOFF_MS = 750
const MAX_BACKOFF_MS = 10_000

/**
 * @param resolveUrl async, because the access token has to be fetched per attempt — a token
 *   captured once goes stale across a long-lived reconnect and the socket is refused.
 * @param onEvent called with the event kind.
 * @param onConnectedChange called with true/false; a caller uses it to decide whether its
 *   fallback poll still has to run.
 */
export function openSessionEvents(resolveUrl, onEvent, onConnectedChange = () => {}) {
  let socket = null
  let attempt = 0
  let timer
  let closed = false

  function retry() {
    if (closed) return
    const delay = Math.min(MAX_BACKOFF_MS, BASE_BACKOFF_MS * 2 ** attempt++)
    timer = setTimeout(connect, delay)
  }

  async function connect() {
    if (closed) return
    let url
    try {
      url = await resolveUrl()
    } catch {
      retry()
      return
    }
    // The await above yields, so a close() in the meantime has to be honoured here — otherwise
    // a pane that was torn down reopens a socket nobody is listening to.
    if (closed) return

    try {
      socket = new WebSocket(url)
    } catch {
      retry()
      return
    }

    socket.onopen = () => {
      attempt = 0
      onConnectedChange(true)
    }
    socket.onmessage = event => {
      let message
      try {
        message = JSON.parse(event.data)
      } catch {
        return
      }
      // "ping" only exists to keep an idle connection observable; it is not a change.
      if (message?.type && message.type !== 'ping') onEvent(message.type)
    }
    socket.onclose = () => {
      socket = null
      onConnectedChange(false)
      retry()
    }
    // An error is always followed by a close, so recovery is left to onclose; closing here only
    // makes sure the socket does not sit half-open.
    socket.onerror = () => {
      try { socket?.close() } catch { /* already gone */ }
    }
  }

  connect()

  return {
    close() {
      closed = true
      clearTimeout(timer)
      if (!socket) return
      // Detached first: otherwise our own close triggers the reconnect path.
      socket.onclose = null
      socket.onerror = null
      try { socket.close() } catch { /* already gone */ }
      socket = null
    }
  }
}
