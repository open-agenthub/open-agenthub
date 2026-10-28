// Control messages occupy their own WebSocket frame; PTY output, including JSON printed by
// tools, otherwise keeps its existing terminal path. Subscribe explicitly for older runtimes.
export function parseActivityFrame(data) {
  if (typeof data !== 'string' || !data.startsWith('{')) return null
  try {
    const frame = JSON.parse(data)
    return frame?.type === 'agenthub' && frame.subtype === 'activity' &&
      ['working', 'waiting', 'idle', 'unknown'].includes(frame.state) &&
      ['hooks', 'output'].includes(frame.source)
      ? { state: frame.state, source: frame.source,
          ...(typeof frame.ready === 'boolean' ? { ready: frame.ready } : {}),
          ...(['approval', 'answer'].includes(frame.reason) ? { reason: frame.reason } : {}),
          ...(frame.reason === 'answer' && Array.isArray(frame.questions) ? { questions: questionsOf(frame.questions) } : {}) }
      : null
  } catch { return null }
}

// The open question tool's questions as the runtime relays them from the tool's input (#343).
// Only strings get through: they end up as text in the notice, never as markup.
function questionsOf(list) {
  const text = value => typeof value === 'string' ? value : ''
  return list.slice(0, 8).filter(entry => entry && text(entry.question).trim()).map(entry => ({
    header: text(entry.header), question: text(entry.question), multiSelect: entry.multiSelect === true,
    options: (Array.isArray(entry.options) ? entry.options : []).slice(0, 16).filter(option => text(option?.label).trim())
      .map(option => ({ label: text(option.label), description: text(option.description) }))
  }))
}

export function parsePersistenceFrame(data) {
  if (typeof data !== 'string' || !data.startsWith('{')) return null
  try {
    const frame = JSON.parse(data)
    return frame?.type === 'agenthub' && frame.subtype === 'persistence' &&
      typeof frame.withheld === 'boolean' ? { withheld: frame.withheld } : null
  } catch { return null }
}

// A context-clear handover the runtime could not deliver yet (#336): the fresh conversation would
// otherwise start without it and nothing outside the terminal would say so.
export function parseHandoverFrame(data) {
  if (typeof data !== 'string' || !data.startsWith('{')) return null
  try {
    const frame = JSON.parse(data)
    if (frame?.type !== 'agenthub' || frame.subtype !== 'handover' || typeof frame.failed !== 'boolean') return null
    if (!frame.failed) return { failed: false }
    const text = value => typeof value === 'string' ? value.slice(0, 200) : ''
    return { failed: true, reason: text(frame.reason), next: text(frame.next) }
  } catch { return null }
}
