// The live switch end to end in the pod (#478): the branch's REAL common/server.js with the REAL
// driver starts the REAL pinned CLI in a PTY, a Workspace-like client types turns over the agent
// socket, and the switch goes through POST /agenthub/model exactly as the hub sends it. The mock
// model's answers say which model each turn reached and whether the first turn was still in it.
//
// usage: node session-agent-e2e.mjs <claude|codex|opencode|openclaw> [outdir]
// env:   CLIS, XTERM as for cli-switch.mjs; PROOF_RUNTIME=<worktree>/agent-runtime (default: ../../agent-runtime).
//        Not RUNTIME: a session pod exports that for its own session agent, under /opt/session-agent.
//
// Per run it records, in <outdir>/result.json: the switch answer, the model of the turn after it,
// whether that turn still carried the first one, the refusal while a turn runs (and that nothing
// was typed for it), and the refusal of an id that is not one.
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { MARKER, SLOW, prepareAgent, startMock } from './fixtures.mjs'

const require = createRequire(import.meta.url)
const { Terminal } = require(path.join(process.env.XTERM || '.', 'node_modules/@xterm/headless'))
const WebSocket = require('/opt/session-agent/node_modules/ws')

const here = path.dirname(new URL(import.meta.url).pathname)
const RUNTIME = path.resolve(process.env.PROOF_RUNTIME || path.join(here, '../../agent-runtime'))
const CLIS = path.join(process.env.CLIS || '.', 'node_modules/.bin')
const [agent, outArg] = process.argv.slice(2)
const out = path.resolve(outArg || 'e2e-' + agent)
fs.rmSync(out, { recursive: true, force: true })
fs.mkdirSync(path.join(out, 'tmp'), { recursive: true })

const PLAN = {
  claude: { driver: 'claude', ready: /for shortcuts|auto mode/, target: 'claude-sonnet-5-5', sent: 'claude-sonnet-5-5', other: 'claude-haiku-5-5' },
  codex: { driver: 'codex', ready: /Ask Codex/, target: 'gpt-6-luna', sent: 'gpt-6-luna', other: 'gpt-6-sol' },
  opencode: { driver: 'opencode', ready: /Ask anything/, target: 'mock/mock-b', sent: 'mock-b', other: 'mock/mock-a' },
  openclaw: { driver: 'openclaw', ready: /local ready/, target: 'mock/mock-b', sent: 'mock-b', other: 'mock/mock-a' }
}[agent]
if (!PLAN) throw new Error('agent must be claude, codex, opencode or openclaw')

const log = path.join(out, 'model.jsonl')
const mock = await startMock({ log, slowMs: 9_000 })
const requests = () => fs.readFileSync(log, 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line))

// The hub the session agent reports to: every call is logged by path, every answer is empty.
const hubCalls = []
const hub = http.createServer((req, res) => {
  req.resume()
  req.on('end', () => {
    hubCalls.push(req.method + ' ' + req.url.split('?')[0])
    res.writeHead(200, { 'content-type': 'application/json' })
    res.end(req.url.includes('/messages') ? '{"messages":[]}' : '{}')
  })
})
await new Promise(resolve => hub.listen(0, '127.0.0.1', resolve))

const home = fs.mkdtempSync(path.join(out, 'home-'))
const work = fs.mkdtempSync(path.join(out, 'work-'))
const sessionId = crypto.randomUUID()
const prepared = prepareAgent(agent, { home, work, port: mock.port, clis: CLIS, sessionId })
const token = 'proof-' + crypto.randomBytes(8).toString('hex')
const agentPort = await new Promise(resolve => { const s = http.createServer().listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => resolve(p)) }) })
const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW)/.test(k)))
const env = {
  ...base, ...prepared.env, HOME: home, TERM: 'xterm-256color', TMPDIR: path.join(out, 'tmp'),
  PATH: CLIS + ':' + process.env.PATH,
  AGENTHUB_DRIVER: path.join(RUNTIME, PLAN.driver, 'driver.js'),
  AGENTHUB_PORT: String(agentPort), AGENTHUB_MODE: 'interactive', AGENTHUB_WORKDIR: work,
  AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${hub.address().port}/internal/sessions/s1`,
  AGENTHUB_CALLBACK_TOKEN: token, AGENTHUB_SESSION_ID: 's1', AGENTHUB_CLAUDE_SESSION_ID: sessionId,
  // Never this pod's own watcher or restore marker: the session agent signals the pid it finds.
  AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(out, 'watcher.pid'), AGENTHUB_STATE_RESTORED: '0'
}
const agentLog = fs.openSync(path.join(out, 'session-agent.log'), 'w')
const server = spawn(process.execPath, [path.join(RUNTIME, 'common', 'server.js')], { env, cwd: work, stdio: ['ignore', agentLog, agentLog] })

const wait = ms => new Promise(resolve => setTimeout(resolve, ms))
const term = new Terminal({ cols: 120, rows: 40, allowProposedApi: true, scrollback: 2000 })
const screen = () => {
  const buffer = term.buffer.active
  const lines = []
  for (let y = 0; y < term.rows; y++) lines.push(buffer.getLine(buffer.viewportY + y)?.translateToString(true) ?? '')
  return lines.join('\n')
}
const dump = name => fs.writeFileSync(path.join(out, name + '.txt'), screen())
async function until(test, ms, what) {
  const end = Date.now() + ms
  while (Date.now() < end) { if (test()) return true; await wait(200) }
  console.log('!! timed out waiting for ' + what)
  return false
}

let socket
for (let attempt = 0; attempt < 50 && !socket; attempt++) {
  await wait(300)
  socket = await new Promise(resolve => {
    const ws = new WebSocket(`ws://127.0.0.1:${agentPort}/`)
    ws.on('open', () => resolve(ws))
    ws.on('error', () => resolve(null))
  })
}
if (!socket) throw new Error('session agent did not listen; see ' + path.join(out, 'session-agent.log'))
socket.on('message', data => { const text = data.toString(); if (!text.startsWith('{"type":"agenthub"')) term.write(text) })
socket.send(JSON.stringify({ type: 'resize', cols: 120, rows: 40 }))
term.onData(data => socket.send(JSON.stringify({ type: 'input', data })))
// The Workspace composer's path: one bracketed paste, Enter as its own key after a pause.
async function sendTurn(text) {
  socket.send(JSON.stringify({ type: 'input', data: '\x1b[200~' + text + '\x1b[201~' }))
  await wait(300)
  socket.send(JSON.stringify({ type: 'input', data: '\r' }))
}
async function switchTo(model) {
  const started = Date.now()
  const response = await fetch(`http://127.0.0.1:${agentPort}/agenthub/model`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token }, body: JSON.stringify({ model })
  })
  return { status: response.status, body: await response.json().catch(() => null), ms: Date.now() - started }
}

const result = { agent, cli: prepared.cmd }
await until(() => PLAN.ready.test(screen()), 120_000, 'the CLI to draw its prompt')
await wait(2_000)
dump('1-ready')

await sendTurn(`Remember the word ${MARKER}. Reply briefly.`)
await until(() => /MODEL=/.test(screen()), 60_000, 'the first answer')
await wait(2_500)
dump('2-turn1')
result.firstTurn = requests().filter(r => r.stream).at(-1)

const turnsBeforeSwitch = requests().filter(r => r.stream).length
result.switch = await switchTo(PLAN.target)
await wait(1_000)
dump('3-after-switch')

// The switch itself starts no turn: nothing it typed reached the model as a prompt.
result.switchStartedNoTurn = requests().filter(r => r.stream).length === turnsBeforeSwitch
const before = requests().length
const question = 'Which word did I ask you to remember?'
await sendTurn(question)
await until(() => requests().slice(before).some(r => r.stream && r.model === PLAN.sent), 60_000, 'the turn after the switch')
await wait(2_500)
dump('4-turn2')
// The turn's own request: Claude Code also sends side requests (a prompt suggestion) afterwards.
const second = requests().slice(before).filter(r => r.stream && r.lastUserText.includes(question)).at(-1)
result.turnAfterSwitch = second
result.contextKept = !!second?.markerInRequest
result.reachedNewModel = second?.model === PLAN.sent
// And it left nothing in the prompt box: the person's text ends the turn on a line of its own,
// with nothing typed in front of it. What comes before may be the CLI's own record — Claude Code
// adds its local command and output (<local-command-stdout>Set model to …) to the next message,
// as it does for any /model typed in its terminal; OpenClaw prefixes a timestamp.
result.promptLeftClean = !!second && new RegExp('(^|\\n|\\] )' + question.replace(/[?.]/g, '\\$&') + '$').test(second.lastUserText)

// A switch while a turn runs: refused, and nothing typed into the CLI for it.
const beforeSlow = requests().length
await sendTurn(`${SLOW}: take your time.`)
await until(() => requests().length > beforeSlow, 30_000, 'the slow turn to reach the model')
await wait(2_000)
dump('5-turn-running')
const typedBefore = screen()
result.whileRunning = await switchTo(PLAN.other)
await wait(500)
result.nothingTypedWhileRunning = !screen().includes('/model ' + PLAN.other) && !typedBefore.includes('/model ' + PLAN.other)
await until(() => (screen().match(/MODEL=/g) || []).length >= 3, 30_000, 'the slow turn to end')
await wait(2_500)
result.slowTurnModel = requests().slice(beforeSlow).filter(r => r.stream).at(-1)?.model

result.invalidId = await switchTo('two words')
result.wrongToken = (await fetch(`http://127.0.0.1:${agentPort}/agenthub/model`, {
  method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Agent-Token': 'nope' }, body: '{"model":"x"}' })).status
dump('6-end')
result.hubCalls = [...new Set(hubCalls)]
result.ok = result.switch.status === 200 && result.reachedNewModel && result.contextKept &&
  result.switchStartedNoTurn && result.promptLeftClean &&
  result.whileRunning.status === 409 && result.nothingTypedWhileRunning && result.slowTurnModel === PLAN.sent &&
  result.invalidId.status === 400 && result.wrongToken === 401
fs.writeFileSync(path.join(out, 'result.json'), JSON.stringify(result, null, 2))
console.log(JSON.stringify(result, null, 2))

socket.close()
server.kill('SIGTERM')
await wait(3_000)
try { server.kill('SIGKILL') } catch {}
mock.close()
hub.close()
process.exit(result.ok ? 0 : 1)
