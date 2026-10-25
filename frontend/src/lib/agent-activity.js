// Control messages occupy their own WebSocket frame; PTY output, including JSON printed by
// tools, otherwise keeps its existing terminal path. Subscribe explicitly for older runtimes.
export function parseActivityFrame(data) {
  if (typeof data !== 'string' || !data.startsWith('{')) return null
  try {
    const frame = JSON.parse(data)
    return frame?.type === 'agenthub' && frame.subtype === 'activity' &&
      ['working', 'waiting', 'idle', 'unknown'].includes(frame.state) &&
      ['hooks', 'output'].includes(frame.source)
      ? { state: frame.state, source: frame.source, ...(['approval', 'answer'].includes(frame.reason) ? { reason: frame.reason } : {}) }
      : null
  } catch { return null }
}
