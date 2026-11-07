// Does the pinned CLI switch its model inside a running conversation, and does the conversation
// survive the switch? The real CLI runs in a real PTY (node-pty from the session agent), rendered
// by @xterm/headless, which answers the TUI's terminal queries as the browser's xterm does. The
// mock model answers every turn with the model id it was asked for and whether the first turn's
// marker word is still in the request, so a screen dump alone shows both facts.
//
// usage: node cli-switch.mjs <claude|codex|opencode|openclaw> '<steps json>' [outdir]
// steps: {"paste":"text"} composer path (bracketed paste, 150 ms, CR) | {"type":"/mo"} key by key
//        {"keys":"\u001b"} | {"wait":ms} | {"dump":"name"} | {"ready":"text|other text","ms":N}
// env:   CLIS=<dir with node_modules/.bin/{codex,opencode,openclaw}>  XTERM=<dir with @xterm/headless>
//        START_MODEL, CUSTOM_ENDPOINT=1, REUSE=<earlier outdir> with EXTRA_ARGS='["resume","--last"]'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { prepareAgent, startMock } from './fixtures.mjs'

const require = createRequire(import.meta.url)
const pty = require(process.env.NODE_PTY || '/opt/session-agent/node_modules/node-pty')
const { Terminal } = require(path.join(process.env.XTERM || '.', 'node_modules/@xterm/headless'))

const CLIS = path.join(process.env.CLIS || '.', 'node_modules/.bin')
const [agent, stepsJson, outArg] = process.argv.slice(2)
const steps = JSON.parse(stepsJson || '[]')
const out = path.resolve(outArg || 'out-' + agent)
fs.mkdirSync(out, { recursive: true })
const log = path.join(out, 'model.jsonl')
const mock = await startMock({ log })

// REUSE=<outdir of an earlier run> starts in that run's HOME and work dir: the restart half of
// "switch on restart with resume", with the session's files as the first process left them.
const reuse = process.env.REUSE ? path.resolve(process.env.REUSE) : null
const home = reuse ? fs.readFileSync(path.join(reuse, 'home'), 'utf8') : fs.mkdtempSync(path.join(out, 'home-'))
const work = reuse ? fs.readFileSync(path.join(reuse, 'work'), 'utf8') : fs.mkdtempSync(path.join(out, 'work-'))
const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW)/.test(k)))
const prepared = prepareAgent(agent, { home, work, port: mock.port, clis: CLIS, startModel: process.env.START_MODEL,
  customEndpoint: !!process.env.CUSTOM_ENDPOINT, sessionId: process.env.SESSION_ID })
const env = { ...base, HOME: home, TERM: 'xterm-256color', COLORTERM: 'truecolor', PATH: CLIS + ':' + process.env.PATH, ...prepared.env }
const extra = process.env.EXTRA_ARGS ? JSON.parse(process.env.EXTRA_ARGS) : []
// A resume names its own id; --session-id beside it would start a second conversation.
const args = extra.includes('--resume') ? extra : [...prepared.args, ...extra]
fs.writeFileSync(path.join(out, 'home'), home)
fs.writeFileSync(path.join(out, 'work'), work)

const cols = 120
const rows = 40
const term = new Terminal({ cols, rows, allowProposedApi: true })
const p = pty.spawn(prepared.cmd, args, { name: 'xterm-256color', cols, rows, cwd: work, env })
p.onData(d => term.write(d))
term.onData(d => p.write(d))
let exited = null
p.onExit(e => { exited = e })
const wait = ms => new Promise(r => setTimeout(r, ms))
const screen = () => {
  const b = term.buffer.active
  const lines = []
  for (let y = 0; y < term.rows; y++) lines.push(b.getLine(b.viewportY + y)?.translateToString(true) ?? '')
  return lines.join('\n')
}
const dump = name => {
  fs.writeFileSync(path.join(out, name + '.txt'), screen())
  console.log('==== ' + name + (exited ? ' (exited ' + JSON.stringify(exited) + ')' : '') + '\n' + screen().split('\n').filter(l => l.trim()).join('\n'))
}
// "ready" is a plain text to wait for, with "|" between alternatives — not a regular expression,
// which would be built from the command line.
const shows = alternatives => alternatives.split('|').some(text => screen().includes(text))
for (const step of steps) {
  if (step.ready) {
    const end = Date.now() + (step.ms || 90000)
    while (!shows(step.ready) && Date.now() < end && !exited) await wait(250)
    if (!shows(step.ready)) console.log('!! not ready: ' + step.ready)
    await wait(step.settle ?? 1500)
  } else if (step.paste !== undefined) { p.write('\x1b[200~' + step.paste + '\x1b[201~'); await wait(150); p.write('\r') }
  else if (step.type !== undefined) { for (const ch of step.type) { p.write(ch); await wait(40) } }
  else if (step.keys !== undefined) p.write(step.keys)
  else if (step.wait) await wait(step.wait)
  else if (step.dump) { await wait(300); dump(step.dump) }
}
console.log('==== model requests (' + log + ')\n' + fs.readFileSync(log, 'utf8'))
try { p.kill() } catch {}
await wait(500)
mock.close()
process.exit(0)
