// Scripted stand-in for an Anthropic Messages endpoint, used as the session's "custom model
// endpoint" so the first-session test needs no provider account. Turn 1 answers the user's task
// with one Bash tool call that does what a real agent would do for "change the README and open a
// PR"; turn 2 (after the tool result) reports. Every generating request is logged as one JSON line
// on stdout so `kubectl logs` shows exactly what the CLI sent.
import http from 'node:http'

const TASK_SCRIPT = process.env.TASK_SCRIPT || 'echo no TASK_SCRIPT'
let n = 0
http.createServer((req, res) => {
  let raw = ''
  req.on('data', c => raw += c)
  req.on('end', () => {
    let body = {}; try { body = JSON.parse(raw || '{}') } catch {}
    if (req.method === 'GET') {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ data: [{ id: 'audit-model', type: 'model', display_name: 'audit-model' }], has_more: false }))
    }
    if (req.url.includes('count_tokens')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end('{"input_tokens":10}') }
    const msgs = body.messages || []
    const last = msgs[msgs.length - 1] || {}
    const lastText = JSON.stringify(last.content || '')
    const hasToolResult = Array.isArray(last.content) && last.content.some(b => b.type === 'tool_result')
    // The CLI may append a context message after the user's prompt, so look at the whole turn.
    const isTask = JSON.stringify(msgs).includes('AUDIT-TASK')
    n++
    console.log(JSON.stringify({ at: new Date().toISOString(), n, url: req.url, stream: !!body.stream, tools: (body.tools || []).length, isTask, hasToolResult, auth: req.headers['x-api-key'] ? 'x-api-key' : req.headers.authorization ? 'bearer' : 'none', toolNames: (body.tools || []).map(t => t.name), msgs: msgs.length, lastText: lastText.slice(-400) }))
    let content
    if (isTask && !hasToolResult) content = [{ type: 'tool_use', id: 'toolu_audit' + n, name: 'Bash', input: { command: TASK_SCRIPT, description: 'Make the README change, push a branch, open a PR', timeout: 600000 } }]
    else if (hasToolResult) {
      const out = JSON.stringify(last.content.find(b => b.type === 'tool_result')?.content || '').slice(0, 1500)
      content = [{ type: 'text', text: 'AUDIT-DONE. Tool output: ' + out }]
    } else content = [{ type: 'text', text: 'ok' }]
    const stop = content[0].type === 'tool_use' ? 'tool_use' : 'end_turn'
    const msg = { id: 'msg_' + n, type: 'message', role: 'assistant', model: body.model || 'audit-model', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } }
    if (!body.stream) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ ...msg, content, stop_reason: stop })) }
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const ev = (t, d) => res.write(`event: ${t}\ndata: ${JSON.stringify({ type: t, ...d })}\n\n`)
    ev('message_start', { message: msg })
    const b = content[0]
    if (b.type === 'tool_use') {
      ev('content_block_start', { index: 0, content_block: { type: 'tool_use', id: b.id, name: b.name, input: {} } })
      ev('content_block_delta', { index: 0, delta: { type: 'input_json_delta', partial_json: JSON.stringify(b.input) } })
    } else {
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: b.text } })
    }
    ev('content_block_stop', { index: 0 })
    ev('message_delta', { delta: { stop_reason: stop, stop_sequence: null }, usage: { output_tokens: 5 } })
    ev('message_stop', {})
    res.end()
  })
}).listen(Number(process.env.PORT || 443), '0.0.0.0', () => console.log('audit mock model listening'))
