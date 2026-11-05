// A Messages API stand-in for the real Claude Code CLI: it records every request it is sent as
// req-N.json in <dir>, so the proof can read exactly which earlier turns a resumed CLI replayed.
// Each answer names the request number, so later requests show which answer they carry.
// Run: node mock-model.mjs <dir> [port]
import fs from 'node:fs'
import http from 'node:http'
const [dir, port = '17690'] = process.argv.slice(2)
fs.mkdirSync(dir, { recursive: true })
let n = 0
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
    // Side requests (title generation, topic detection) carry no tools; only record the turn itself.
    const turn = Array.isArray(json.tools) && json.tools.length > 0
    const id = turn ? ++n : 0
    if (turn) fs.writeFileSync(`${dir}/req-${id}.json`, body)
    const text = turn ? `ANSWER-${id}: noted.` : 'Title'
    const msg = { id: `msg_${id}`, type: 'message', role: 'assistant', model: json.model || 'm', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } }
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
