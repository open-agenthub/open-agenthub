// STAND-IN MODEL for the sales demo (#481). Answers the Anthropic Messages API that Claude Code
// calls when a session uses "Own endpoint", so the demo needs no provider account and runs the
// same way every time. It is scripted, not intelligent: each reply starts with
// "[stand-in model <id>]" so nobody watching mistakes it for a real model, and the id is the model
// the CLI asked for, which makes a mid-session model switch visible in the conversation.
//
// Script, keyed on markers in the conversation:
//   DEMO-ISSUE   -> one Bash call: branch, fix, test, commit, push to the stand-in git host
//   DEMO-TEST    -> one Bash call: add a regression test, commit, push (the collaborator's ask)
//   DEMO-SUMMARY -> a text summary (asked after the model switch)
// Each generating request is logged as one JSON line, so `kubectl logs` shows what the CLI sent.
import http from 'node:http'

const MODELS = [
  { id: 'standin-fast', display_name: 'Stand-in fast (demo)' },
  { id: 'standin-deep', display_name: 'Stand-in deep (demo)' }
]
const FIX = `set -e; cd /workspace/repo
git checkout -q -b fix/42-rounding
sed -i 's/Math.floor(cents)/Math.round(cents)/' src/total.js
node test/total.test.js
git -c user.name=agent -c user.email=agent@example.com commit -qam "fix: round invoice totals to the nearest cent (#42)"
git push -q -u origin fix/42-rounding 2>&1
git log --oneline -1; git ls-remote --heads origin fix/42-rounding`
const TEST = `set -e; cd /workspace/repo
cat >> test/total.test.js <<'JS'
assert.strictEqual(total([{ price: 2.01, qty: 3 }]), 6.03) // regression for #42
console.log('regression test for #42 passed')
JS
node test/total.test.js
git -c user.name=agent -c user.email=agent@example.com commit -qam "test: regression test for #42"
git push -q 2>&1; git log --oneline -2`

let n = 0
function reply(body) {
  const msgs = body.messages || []
  const last = msgs[msgs.length - 1] || {}
  const all = JSON.stringify(msgs)
  const lastUser = [...msgs].reverse().find(m => m.role === 'user' && !(Array.isArray(m.content) && m.content.some(b => b.type === 'tool_result')))
  const asked = JSON.stringify(lastUser?.content || '')
  const toolResult = Array.isArray(last.content) && last.content.find(b => b.type === 'tool_result')
  const tag = `[stand-in model ${body.model || '?'}] `
  if (toolResult) {
    const out = JSON.stringify(toolResult.content || '').slice(0, 600)
    return [{ type: 'text', text: tag + (asked.includes('DEMO-TEST')
      ? 'Added a regression test for #42 and pushed it to fix/42-rounding.'
      : 'Fixed #42: totals now round to the nearest cent instead of rounding down. Tests pass; the branch fix/42-rounding is pushed and ready for review.') + '\n\nTool output: ' + out }]
  }
  if (asked.includes('DEMO-ISSUE')) return [{ type: 'tool_use', id: 'toolu_fix' + n, name: 'Bash', input: { command: FIX, description: 'Fix #42 on a branch, run the tests and push' } }]
  if (asked.includes('DEMO-TEST')) return [{ type: 'tool_use', id: 'toolu_test' + n, name: 'Bash', input: { command: TEST, description: 'Add a regression test and push' } }]
  if (asked.includes('DEMO-SUMMARY')) return [{ type: 'text', text: tag + 'Summary for the reviewer: fix/42-rounding changes one line in src/total.js (floor → round) and adds a regression test. ' + (all.includes('fix/42-rounding') ? 'Earlier turns are still in my context.' : '') }]
  return [{ type: 'text', text: tag + 'Ready.' }]
}

http.createServer((req, res) => {
  let raw = ''
  req.on('data', c => raw += c)
  req.on('end', () => {
    let body = {}; try { body = JSON.parse(raw || '{}') } catch {}
    if (req.method === 'GET') {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ data: MODELS.map(m => ({ ...m, type: 'model', created_at: '2026-01-01T00:00:00Z' })), has_more: false, first_id: MODELS[0].id, last_id: MODELS[1].id }))
    }
    if (req.url.includes('count_tokens')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end('{"input_tokens":10}') }
    n++
    const content = reply(body)
    console.log(JSON.stringify({ at: new Date().toISOString(), n, model: body.model, stream: !!body.stream, msgs: (body.messages || []).length, reply: content[0].type === 'tool_use' ? 'tool_use' : content[0].text.slice(0, 80) }))
    const stop = content[0].type === 'tool_use' ? 'tool_use' : 'end_turn'
    const usage = { input_tokens: 1200, output_tokens: 300 }
    const msg = { id: 'msg_' + n, type: 'message', role: 'assistant', model: body.model || 'standin-fast', content: [], stop_reason: null, stop_sequence: null, usage }
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
    ev('message_delta', { delta: { stop_reason: stop, stop_sequence: null }, usage: { output_tokens: 300 } })
    ev('message_stop', {})
    res.end()
  })
}).listen(Number(process.env.PORT || 443), '0.0.0.0', () => console.log('stand-in model listening'))
