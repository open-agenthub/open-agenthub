// Reducer for the Claude stream-json event log a chat session produces. Feed it
// raw WebSocket frames or transcript text; it maintains a renderable item list.
// Trimmed scrollback can start mid-line, so unparseable lines are skipped.

function textOf(content) {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return ''
  return content.filter(block => block?.type === 'text' && typeof block.text === 'string')
    .map(block => block.text).join('\n')
}

export function createChatLog() {
  const toolsById = new Map()

  const log = {
    items: [],
    drafts: [],
    busy: false,
    model: '',
    lastResult: null,

    feed(text) {
      for (const line of String(text).split('\n')) {
        const trimmed = line.trim()
        if (!trimmed) continue
        let event
        try { event = JSON.parse(trimmed) } catch { continue }
        if (event && typeof event.type === 'string') handle(event)
      }
    },

    reset() {
      log.items = []
      log.drafts = []
      log.busy = false
      log.model = ''
      log.lastResult = null
      toolsById.clear()
    }
  }

  function pushNote(kind, text) {
    const last = log.items[log.items.length - 1]
    if (last && last.kind === kind && kind === 'stderr') last.text += text
    else log.items.push({ kind, text })
  }

  function assistantItem(messageId) {
    const last = log.items[log.items.length - 1]
    if (last && last.kind === 'assistant' && last.id === messageId) return last
    const item = { kind: 'assistant', id: messageId, blocks: [] }
    log.items.push(item)
    return item
  }

  function handleAssistant(event) {
    const message = event.message || {}
    const item = assistantItem(message.id || '')
    for (const block of Array.isArray(message.content) ? message.content : []) {
      if (block.type === 'text' || block.type === 'thinking') {
        item.blocks.push({ type: block.type, text: block.type === 'thinking' ? block.thinking : block.text })
      } else if (block.type === 'tool_use') {
        const tool = { type: 'tool', id: block.id, name: block.name, input: block.input, result: null, isError: false }
        if (block.id) toolsById.set(block.id, tool)
        item.blocks.push(tool)
      }
    }
    log.drafts = []
    log.busy = true
  }

  function handleUser(event) {
    const content = event.message?.content
    const results = Array.isArray(content) ? content.filter(block => block?.type === 'tool_result') : []
    if (results.length) {
      for (const block of results) {
        const tool = toolsById.get(block.tool_use_id)
        if (!tool) continue
        tool.result = textOf(block.content)
        tool.isError = block.is_error === true
      }
      return
    }
    const text = textOf(content)
    if (text) {
      log.items.push({ kind: 'user', text })
      log.busy = true
    }
  }

  function handleStream(event) {
    const inner = event.event || {}
    if (inner.type === 'content_block_start') {
      log.drafts.push({ index: inner.index, type: inner.content_block?.type || 'text', text: '' })
      log.busy = true
    } else if (inner.type === 'content_block_delta') {
      const draft = log.drafts.find(d => d.index === inner.index)
      const delta = inner.delta || {}
      const text = delta.type === 'thinking_delta' ? delta.thinking : delta.type === 'text_delta' ? delta.text : ''
      if (draft && text) draft.text += text
      log.busy = true
    } else if (inner.type === 'message_stop') {
      log.drafts = []
    }
  }

  function handle(event) {
    if (event.type === 'assistant') handleAssistant(event)
    else if (event.type === 'user') handleUser(event)
    else if (event.type === 'stream_event') handleStream(event)
    else if (event.type === 'result') {
      log.drafts = []
      log.busy = false
      log.lastResult = {
        isError: event.is_error === true,
        costUsd: typeof event.total_cost_usd === 'number' ? event.total_cost_usd : null,
        durationMs: typeof event.duration_api_ms === 'number' ? event.duration_api_ms : null
      }
    } else if (event.type === 'system') {
      if (event.subtype === 'init' && event.model) log.model = event.model
    } else if (event.type === 'agenthub') {
      if (event.subtype === 'stderr') pushNote('stderr', event.text || '')
      else if (event.subtype === 'info') pushNote('info', event.text || '')
      else if (event.subtype === 'exit') {
        log.busy = false
        log.drafts = []
        log.items.push({ kind: 'exit', code: event.code ?? 0, text: `Session ended (code ${event.code ?? 0}).` })
      }
    }
  }

  return log
}
