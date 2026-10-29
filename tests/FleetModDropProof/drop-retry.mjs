// #340 proof: a settings UserPromptSubmit hook blocks the first prompt (the mod's release), so
// Claude Code resolves $.prompt.submit to { drop }. Does the message reach the model on a later
// idle poll? Based on the #98 rig: real Claude Code (interactive, in a PTY via script(1)) with the fleet mod
// loaded from MOD_DIR (port rewritten), a stand-in session agent that queues and hands over
// messages exactly like common/server.js (GET hands over and clears), and a mock Messages API
// that records what the model was sent. Sends a plain message while the CLI sits idle.
import http from 'node:http'
import { spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

const MOD_DIR = process.argv[2]
const SEND = process.argv[3] || 'plain'   // plain | priority
const BUSY = process.argv[4] === 'busy'   // a person's turn is running when the message lands
const CLAUDE_BIN = process.env.CLAUDE_BIN || 'claude'
const DROPS = Number(process.env.DROPS ?? 1)   // prompts the settings hook blocks before letting one through
const AGENT_PORT = 17681, API_PORT = 17682, TOKEN = 'mod-secret'
const log = (...a) => console.log(new Date().toISOString().slice(11, 23), ...a)

let queue = [], handedOver = [], polls = 0
http.createServer((req, res) => {
  if (req.headers.authorization !== 'Bearer ' + TOKEN) { res.writeHead(401); return res.end() }
  if (req.url === '/agenthub/mod/heartbeat') { log('session agent: mod heartbeat'); res.writeHead(204); return res.end() }
  if (req.url === '/agenthub/mod/inbox') {
    const messages = queue; queue = []; polls++
    if (messages.length) { handedOver.push(...messages); log('session agent: handed over', messages.map(m => m.id).join(','), '(hub would have marked them delivered_via=mod)') }
    res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ messages }))
  }
  if (req.url === '/agenthub/mod/limit') { res.writeHead(202); return res.end('{}') }
  res.writeHead(404); res.end()
}).listen(AGENT_PORT, '127.0.0.1')

const modelRequests = []
http.createServer((req, res) => {
  let body = ''
  req.on('data', c => body += c)
  req.on('end', () => {
    let json = {}; try { json = JSON.parse(body) } catch {}
    if (!req.url.startsWith('/v1/messages') || req.url.includes('count_tokens')) {
      res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ input_tokens: 10 }))
    }
    const text = JSON.stringify(json.messages || [])
    modelRequests.push(text); fs.writeFileSync('req-' + modelRequests.length + '.json', body)
    log('model API: request #' + modelRequests.length, text.includes('AgentHub message') ? 'CONTAINS the fleet message' : 'without fleet message')
    const msg = { id: 'msg_1', type: 'message', role: 'assistant', model: json.model || 'm', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } }
    if (!json.stream) {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ ...msg, content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn' }))
    }
    if (BUSY && modelRequests.length === 1) { log('model API: holding the person\'s turn for 10 s'); setTimeout(() => stream(res, json, msg), 10000); return }
    stream(res, json, msg)
  })
}).listen(API_PORT, '127.0.0.1')

function stream(res, json, msg) {
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
    ev('message_start', { message: msg })
    ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
    ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'Noted the fleet message.' } })
    ev('content_block_stop', { index: 0 })
    ev('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } })
    ev('message_stop', {})
    res.end()
}

const home = fs.mkdtempSync(path.join(process.cwd(), 'home-'))
const work = fs.mkdtempSync(path.join(process.cwd(), 'work-'))
const key = 'sk-ant-api03-' + 'x'.repeat(80)
fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({
  hasCompletedOnboarding: true, theme: 'dark',
  customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
  projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true } }
}))
// exit 2 from UserPromptSubmit blocks the prompt; Claude Code then answers the mod's submit
// with { drop: <stderr> }. The counter file lets the next prompt through.
const counter = path.join(home, 'drops')
fs.writeFileSync(path.join(home, 'block-hook.sh'), `#!/bin/sh
cat >> ${home}/hook-input.log; echo >> ${home}/hook-input.log
n=$(cat ${counter} 2>/dev/null || echo 0)
if [ "$n" -lt ${DROPS} ]; then echo $((n+1)) > ${counter}; echo "blocked by the #340 proof hook ($((n+1)))" >&2; exit 2; fi
exit 0
`, { mode: 0o755 })
fs.writeFileSync(path.join(home, 'settings.json'), JSON.stringify({ hooks: { UserPromptSubmit: [{ hooks: [{ type: 'command', command: path.join(home, 'block-hook.sh') }] }] } }))
const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC)/.test(k)))
const env = { ...base, CLAUDE_CONFIG_DIR: home, HOME: home, ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + API_PORT,
  AGENTHUB_MOD_TOKEN: TOKEN, TERM: 'xterm-256color', COLUMNS: '120', LINES: '40' }
const cli = spawn('script', ['-qfc', `stty cols 120 rows 40; exec ${CLAUDE_BIN} --debug-file ${process.cwd()}/debug.log --plugin-dir ${MOD_DIR}`, '/dev/null'], { cwd: work, env, stdio: ['pipe', 'pipe', 'pipe'] })
let screen = ''
cli.stdout.on('data', d => { screen += d.toString() })
cli.stderr.on('data', d => { screen += d.toString() })

const strip = s => s.replace(/\x1b\[[0-9;?]*[ -\/]*[@-~]/g, '').replace(/\x1b\][^\x07]*\x07/g, '')
const wait = ms => new Promise(r => setTimeout(r, ms))
const until = async (cond, ms) => { const end = Date.now() + ms; while (!cond() && Date.now() < end) await wait(200) }
await until(() => polls > 0, 45000)
await wait(3000)
if (BUSY) { cli.stdin.write('hello'); await wait(400); cli.stdin.write('\r'); await until(() => modelRequests.length > 0, 20000); await wait(1000) }
log('CLI up; mod polls so far:', polls, '; model requests so far:', modelRequests.length, '; sending a', SEND, 'message while idle')
queue.push({ id: 'm-1', from: 'child-1', fromTitle: 'Child Worker', body: 'Report: task done, PR #123 is green', priority: SEND === 'priority', interrupt: false })
await wait(BUSY ? 30000 : 20000)
log('UserPromptSubmit hook calls:', (fs.readFileSync(path.join(home, 'hook-input.log'), 'utf8').match(/hook_event_name/g) || []).length, '; blocked:', fs.existsSync(counter) ? fs.readFileSync(counter, 'utf8').trim() : 0)
log('debug.log fleet lines:\n  ' + fs.readFileSync('debug.log', 'utf8').split('\n').filter(l => /fleet|agenthub-fleet/.test(l)).slice(-8).join('\n  '))
log('model requests in order:', modelRequests.map((t, i) => '#' + (i + 1) + (t.includes('AgentHub message') ? ' fleet' : ' plain')).join(' '))
const shown = modelRequests.some(t => t.includes('AgentHub message'))
log('after the wait: handed over =', handedOver.length, '; model saw it =', shown)
const tail = strip(screen).split(/\r?\n/).filter(l => /fleet|AgentHub|Noted/.test(l)).slice(-6)
log('screen lines mentioning it:\n  ' + tail.join('\n  '))
fs.writeFileSync('screen.txt', strip(screen))
cli.kill('SIGTERM'); try { process.kill(-cli.pid) } catch {}
await wait(500)
process.exit(shown ? 0 : 1)
