// A Messages API stand-in for the real Claude Code CLI. Each turn answers "RECEIVED: <the last
// user text>" and logs that text to <dir>/model.log, so the proof can tell which person's message
// reached the model, independent of what any browser shows.
// Run: node mock-model.mjs <dir> [port]
import fs from 'node:fs'
import http from 'node:http'
const [dir, port = '17695'] = process.argv.slice(2)
fs.mkdirSync(dir, { recursive: true })
const lastUserText = json => {
  const users = (json.messages || []).filter(m => m.role === 'user')
  const content = users.at(-1)?.content
  const blocks = typeof content === 'string' ? [{ type: 'text', text: content }] : content || []
  // Claude Code prepends system reminders as their own text blocks; the person's text is the last.
  return blocks.filter(b => b.type === 'text' && !b.text.startsWith('<system-reminder>')).map(b => b.text).at(-1) || ''
}
http.createServer((req, res) => {
  let body = ''
  req.on('data', c => { body += c })
  req.on('end', () => {
    let json = {}
    try { json = JSON.parse(body) } catch { }
    if (!req.url.startsWith('/v1/messages') || req.url.includes('count_tokens')) {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ input_tokens: 10 }))
    }
    const turn = Array.isArray(json.tools) && json.tools.length > 0
    const said = turn ? lastUserText(json) : ''
    if (turn) fs.appendFileSync(`${dir}/model.log`, `${new Date().toISOString()} ${JSON.stringify(said)}\n`)
    const text = turn ? `RECEIVED: ${said.replace(/\s+/g, ' ').slice(0, 200)}` : 'Title'
    const msg = { id: 'msg_' + Date.now(), type: 'message', role: 'assistant', model: json.model || 'm', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } }
    if (!json.stream) {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ ...msg, content: [{ type: 'text', text }], stop_reason: 'end_turn' }))
    }
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const ev = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`)
    ev('message_start', { message: msg })
    ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
    ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text } })
    ev('content_block_stop', { index: 0 })
    ev('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } })
    ev('message_stop', {})
    res.end()
  })
}).listen(Number(port), '127.0.0.1', () => console.log(`mock model on ${port}`))
