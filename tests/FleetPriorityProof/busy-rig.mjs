// #390 rig: the real Claude Code (interactive, PTY via script(1)) with the fleet mod from
// MOD_DIR, a stand-in mod inbox with common/server.js's hand-over semantics, and a mock
// Messages API that keeps the turn busy with STEPS long Bash tool calls (sleep SLEEP_S each).
// A priority message is queued while the first tool call runs. Prints, per model request,
// whether it carried the fleet message, and when the turn ended.
// Usage: node busy-rig.mjs <mod dir> [priority|interrupt]   (env STEPS=3 SLEEP_S=12 WAIT_S=60)
import http from 'node:http'
import { spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

const MOD_DIR = path.resolve(process.argv[2])
const KIND = process.argv[3] || 'priority'
const STEPS = Number(process.env.STEPS || 3), SLEEP_S = Number(process.env.SLEEP_S || 12)
const WAIT_S = Number(process.env.WAIT_S || 60)
const AGENT_PORT = Number(process.env.AGENT_PORT || 17681), API_PORT = Number(process.env.API_PORT || 17682)
const TOKEN = 'mod-secret'
const MARK = 'MSG-390-' + KIND
const t0 = Date.now()
const log = (...a) => console.log(((Date.now() - t0) / 1000).toFixed(1).padStart(6) + 's', ...a)

let queue = [], polls = 0
http.createServer((req, res) => {
  if (req.headers.authorization !== 'Bearer ' + TOKEN) { res.writeHead(401); return res.end() }
  if (req.url === '/agenthub/mod/heartbeat') { res.writeHead(204); return res.end() }
  if (req.url === '/agenthub/mod/inbox') {
    const messages = queue; queue = []; polls++
    if (messages.length) log('inbox: handed', messages.map(m => m.id).join(','), 'to the mod (hub marks delivered_via=mod)')
    res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ messages }))
  }
  if (req.url === '/agenthub/mod/limit') { res.writeHead(202); return res.end('{}') }
  res.writeHead(404); res.end()
}).listen(AGENT_PORT, '127.0.0.1')

const requests = []
let mainCalls = 0, turnEnded = null, toolStarted = null
http.createServer((req, res) => {
  let body = ''
  req.on('data', c => body += c)
  req.on('end', () => {
    let json = {}; try { json = JSON.parse(body) } catch {}
    if (!req.url.startsWith('/v1/messages') || req.url.includes('count_tokens')) {
      res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ input_tokens: 10 }))
    }
    const text = JSON.stringify(json.messages || [])
    const msg = { id: 'msg_' + requests.length, type: 'message', role: 'assistant', model: json.model || 'm', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } }
    if (!json.stream) {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ ...msg, content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn' }))
    }
    // Side requests (title, prompt suggestion) carry no tools; answer them plainly.
    const side = !Array.isArray(json.tools) || !json.tools.length || /Reply with ONLY the suggestion/.test(text)
    requests.push(text); fs.writeFileSync('req-' + requests.length + '.json', body)
    const has = text.includes(MARK)
    log('model: request #' + requests.length + (side ? ' (side)' : '') + (has ? ' CONTAINS ' + MARK : ''))
    if (side) return stream(res, msg, [{ type: 'text', text: 'ok' }], 'end_turn')
    mainCalls++
    if (mainCalls <= STEPS) {
      toolStarted = toolStarted || Date.now()
      return stream(res, msg, [{ type: 'tool_use', id: 'toolu_' + mainCalls, name: 'Bash', input: { command: 'sleep ' + SLEEP_S, description: 'Long step ' + mainCalls } }], 'tool_use')
    }
    turnEnded = turnEnded || Date.now()
    log('model: ending the turn')
    stream(res, msg, [{ type: 'text', text: has ? 'Seen ' + MARK : 'Done.' }], 'end_turn')
  })
}).listen(API_PORT, '127.0.0.1')

function stream(res, msg, blocks, stop) {
  res.writeHead(200, { 'content-type': 'text/event-stream' })
  const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
  ev('message_start', { message: msg })
  blocks.forEach((b, i) => {
    if (b.type === 'text') {
      ev('content_block_start', { index: i, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: i, delta: { type: 'text_delta', text: b.text } })
    } else {
      ev('content_block_start', { index: i, content_block: { type: 'tool_use', id: b.id, name: b.name, input: {} } })
      ev('content_block_delta', { index: i, delta: { type: 'input_json_delta', partial_json: JSON.stringify(b.input) } })
    }
    ev('content_block_stop', { index: i })
  })
  ev('message_delta', { delta: { stop_reason: stop, stop_sequence: null }, usage: { output_tokens: 5 } })
  ev('message_stop', {})
  res.end()
}

const home = fs.mkdtempSync(path.join(process.cwd(), 'home-'))
const work = fs.mkdtempSync(path.join(process.cwd(), 'work-'))
const key = 'sk-ant-api03-' + 'x'.repeat(80)
fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({
  hasCompletedOnboarding: true, theme: 'dark',
  customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
  projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true, allowedTools: ['Bash'] } }
}))
fs.writeFileSync(path.join(home, 'settings.json'), JSON.stringify({ permissions: { allow: ['Bash'] } }))
const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC)/.test(k)))
const env = { ...base, CLAUDE_CONFIG_DIR: home, HOME: home, ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + API_PORT,
  AGENTHUB_MOD_TOKEN: TOKEN, DISABLE_AUTOUPDATER: '1', TERM: 'xterm-256color', COLUMNS: '120', LINES: '40' }
const bin = process.env.CLAUDE_BIN || 'claude'
const cli = spawn('script', ['-qfc', `stty cols 120 rows 40; exec ${bin} --debug-file ${process.cwd()}/debug.log --plugin-dir ${MOD_DIR}`, '/dev/null'], { cwd: work, env, stdio: ['pipe', 'pipe', 'pipe'], detached: true })
let screen = ''
cli.stdout.on('data', d => { screen += d.toString() })
const strip = s => s.replace(/\x1b\[[0-9;?]*[ -\/]*[@-~]/g, '').replace(/\x1b\][^\x07]*\x07/g, '')
const wait = ms => new Promise(r => setTimeout(r, ms))
const until = async (cond, ms) => { const end = Date.now() + ms; while (!cond() && Date.now() < end) await wait(200) }

await until(() => polls > 0, 60000)
await wait(3000)
log('CLI up (mod polled ' + polls + 'x); person submits a prompt that starts a long turn')
cli.stdin.write('do the long job'); await wait(400); cli.stdin.write('\r')
await until(() => toolStarted, 30000)
await wait(3000)
log('tool call running; pushing a ' + KIND + ' message')
queue.push({ id: MARK, from: 'coord-1', fromTitle: 'Coordinator', body: MARK + ': please answer', priority: true, interrupt: KIND === 'interrupt' })
await until(() => requests.some(t => t.includes(MARK)), WAIT_S * 1000)
const first = requests.findIndex(t => t.includes(MARK))
await wait(5000)
log('SUMMARY steps=' + STEPS + ' sleep=' + SLEEP_S + 's kind=' + KIND + ' firstSeenInRequest=' + (first < 0 ? 'never' : '#' + (first + 1)) +
  ' turnEndedAt=' + (turnEnded ? ((turnEnded - t0) / 1000).toFixed(1) + 's' : 'not yet') + ' mainCalls=' + mainCalls)
fs.writeFileSync('screen.txt', strip(screen))
try { process.kill(-cli.pid, 'SIGKILL') } catch {}
process.exit(first < 0 ? 1 : 0)
