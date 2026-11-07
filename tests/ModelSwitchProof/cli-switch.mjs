// Does the pinned CLI switch its model inside a running conversation, and does the conversation
// survive the switch? The real CLI runs in a real PTY (node-pty from the session agent), rendered
// by @xterm/headless, which answers the TUI's terminal queries as the browser's xterm does. A mock
// model answers every turn with the model id it was asked for and whether the first turn's marker
// word is still in the request, so a screen dump alone shows both facts.
//
// usage: node cli-switch.mjs <claude|codex|opencode|openclaw> '<steps json>' [outdir]
// steps: {"paste":"text"} composer path (bracketed paste, 150 ms, CR) | {"type":"/mo"} key by key
//        {"keys":"\u001b"} | {"wait":ms} | {"dump":"name"} | {"ready":"regex","ms":N}
// env:   CLIS=<dir with codex/opencode/openclaw in .bin>  XTERM=<dir with @xterm/headless>
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const pty = require(process.env.NODE_PTY || '/opt/session-agent/node_modules/node-pty')
const { Terminal } = require(path.join(process.env.XTERM || '.', 'node_modules/@xterm/headless'))

const MARKER = 'PAPAYA42'
const CLIS = path.join(process.env.CLIS || '.', 'node_modules/.bin')
const [agent, stepsJson, outArg] = process.argv.slice(2)
const steps = JSON.parse(stepsJson || '[]')
const out = path.resolve(outArg || 'out-' + agent)
fs.mkdirSync(out, { recursive: true })
const log = path.join(out, 'model.jsonl')
fs.writeFileSync(log, '')

// Only the fields the question needs are logged: never a header, so no key lands in the output.
function record(url, json) {
  const text = JSON.stringify(json.messages || json.input || '')
  const entry = { url, model: json.model, stream: !!json.stream, markerInRequest: text.includes(MARKER) }
  fs.appendFileSync(log, JSON.stringify(entry) + '\n')
  return entry
}
const replyFor = e => `MODEL=${e.model} CONTEXT=${e.markerInRequest ? 'KEPT' : 'NONE'}`

const server = http.createServer((req, res) => {
  let body = ''
  req.on('data', c => { body += c })
  req.on('end', () => {
    let json = {}
    try { json = JSON.parse(body) } catch {}
    if (req.url.includes('/models')) {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ object: 'list', data: [{ id: 'mock-a', object: 'model' }, { id: 'mock-b', object: 'model' }], models: [] }))
    }
    const e = record(req.url, json)
    if (req.url.startsWith('/v1/messages')) {
      if (req.url.includes('count_tokens')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end('{"input_tokens":1}') }
      if (!json.stream) {
        res.writeHead(200, { 'content-type': 'application/json' })
        return res.end(JSON.stringify({ id: 'm', type: 'message', role: 'assistant', model: json.model, content: [{ type: 'text', text: replyFor(e) }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } }))
      }
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: json.model, content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: replyFor(e) } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } })
      ev('message_stop', {})
      return res.end()
    }
    if (req.url.includes('/chat/completions')) {
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const chunk = d => res.write('data: ' + JSON.stringify({ id: 'c', object: 'chat.completion.chunk', created: 1, model: json.model, choices: [d] }) + '\n\n')
      chunk({ index: 0, delta: { role: 'assistant', content: replyFor(e) } })
      chunk({ index: 0, delta: {}, finish_reason: 'stop' })
      res.write('data: [DONE]\n\n')
      return res.end()
    }
    if (req.url.includes('/responses')) {
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      const item = { type: 'message', id: 'msg_1', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: replyFor(e), annotations: [] }] }
      ev('response.created', { response: { id: 'resp_1' } })
      ev('response.output_item.done', { output_index: 0, item })
      ev('response.completed', { response: { id: 'resp_1', usage: { input_tokens: 1, input_tokens_details: null, output_tokens: 1, output_tokens_details: null, total_tokens: 2 } } })
      return res.end()
    }
    res.writeHead(404)
    res.end()
  })
})
await new Promise(r => server.listen(0, '127.0.0.1', r))
const PORT = server.address().port

// REUSE=<outdir of an earlier run> starts in that run's HOME and work dir: the restart half of
// "switch on restart with resume", with the session's files as the first process left them.
const reuse = process.env.REUSE ? path.resolve(process.env.REUSE) : null
const home = reuse ? fs.readFileSync(path.join(reuse, 'home'), 'utf8') : fs.mkdtempSync(path.join(out, 'home-'))
const work = reuse ? fs.readFileSync(path.join(reuse, 'work'), 'utf8') : fs.mkdtempSync(path.join(out, 'work-'))
const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW)/.test(k)))
let env = { ...base, HOME: home, TERM: 'xterm-256color', COLORTERM: 'truecolor', PATH: CLIS + ':' + process.env.PATH }
let cmd
let args = []
const mkdir = p => fs.mkdirSync(p, { recursive: true })
if (agent === 'claude') {
  const key = 'sk-ant-api03-' + 'x'.repeat(80)
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({
    hasCompletedOnboarding: true, theme: 'dark',
    customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
    projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true } }
  }))
  env = { ...env, ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + PORT }
  if (process.env.START_MODEL) env.ANTHROPIC_MODEL = process.env.START_MODEL
  cmd = process.env.CLAUDE_BIN || '/usr/local/bin/claude'
  // A resume names its own id; --session-id beside it would start a second conversation.
  args = (process.env.EXTRA_ARGS || '').includes('--resume') ? [] : ['--session-id', process.env.SESSION_ID || crypto.randomUUID()]
} else if (agent === 'codex') {
  mkdir(path.join(home, '.codex'))
  fs.writeFileSync(path.join(home, '.codex/auth.json'), JSON.stringify({ OPENAI_API_KEY: 'sk-test' }))
  // CUSTOM_ENDPOINT=1 writes the provider block the runtime writes for a session's own endpoint
  // (codex/model-provider.js), so the switch is tried against what such a session runs.
  const endpoint = process.env.CUSTOM_ENDPOINT
    ? `model = "${process.env.START_MODEL || 'mock-a'}"\nmodel_provider = "agenthub_endpoint"\n`
    : (process.env.START_MODEL ? `model = "${process.env.START_MODEL}"\n` : '')
  const provider = process.env.CUSTOM_ENDPOINT
    ? `[model_providers.agenthub_endpoint]\nname = "Custom endpoint"\nbase_url = "http://127.0.0.1:${PORT}/v1"\nwire_api = "responses"\n`
    : ''
  fs.writeFileSync(path.join(home, '.codex/config.toml'),
    endpoint + `check_for_update_on_startup = false\ncli_auth_credentials_store = "file"\nopenai_base_url = "http://127.0.0.1:${PORT}/v1"\n${provider}[projects."${work}"]\ntrust_level = "trusted"\n`)
  env = { ...env, OPENAI_API_KEY: 'sk-test' }
  cmd = path.join(CLIS, 'codex')
  args = ['--no-alt-screen', '--no-daemon']
} else if (agent === 'opencode') {
  mkdir(path.join(home, '.config/opencode'))
  fs.writeFileSync(path.join(home, '.config/opencode/opencode.json'), JSON.stringify({
    $schema: 'https://opencode.ai/config.json', autoupdate: false, share: 'disabled',
    model: process.env.START_MODEL || 'mock/mock-a',
    provider: { mock: { npm: '@ai-sdk/openai-compatible', name: 'Mock', options: { baseURL: 'http://127.0.0.1:' + PORT + '/v1', apiKey: 'x' }, models: { 'mock-a': { name: 'Mock A' }, 'mock-b': { name: 'Mock B' } } } }
  }))
  cmd = path.join(CLIS, 'opencode')
} else if (agent === 'openclaw') {
  // OpenClaw takes no endpoint from the environment; a config provider points it at the mock.
  const state = path.join(home, '.openclaw')
  mkdir(state)
  fs.writeFileSync(path.join(state, 'openclaw.json'), JSON.stringify({
    models: { providers: { mock: { baseUrl: 'http://127.0.0.1:' + PORT + '/v1', apiKey: 'mock-key', api: 'openai-completions', models: [{ id: 'mock-a', name: 'mock-a' }, { id: 'mock-b', name: 'mock-b' }] } } },
    agents: { defaults: { model: { primary: process.env.START_MODEL || 'mock/mock-a' }, workspace: work } }
  }, null, 2))
  env = { ...env, OPENCLAW_STATE_DIR: state, OPENCLAW_CONFIG_PATH: path.join(state, 'openclaw.json') }
  cmd = path.join(CLIS, 'openclaw')
  args = ['tui', '--local']
}
if (process.env.EXTRA_ARGS) args = [...args, ...JSON.parse(process.env.EXTRA_ARGS)]
fs.writeFileSync(path.join(out, 'home'), home)
fs.writeFileSync(path.join(out, 'work'), work)

const cols = 120
const rows = 40
const term = new Terminal({ cols, rows, allowProposedApi: true })
const p = pty.spawn(cmd, args, { name: 'xterm-256color', cols, rows, cwd: work, env })
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
for (const step of steps) {
  if (step.ready) {
    const end = Date.now() + (step.ms || 90000)
    while (!new RegExp(step.ready).test(screen()) && Date.now() < end && !exited) await wait(250)
    if (!new RegExp(step.ready).test(screen())) console.log('!! not ready: ' + step.ready)
    await wait(step.settle ?? 1500)
  } else if (step.paste !== undefined) { p.write('\x1b[200~' + step.paste + '\x1b[201~'); await wait(150); p.write('\r') }
  else if (step.type !== undefined) { for (const ch of step.type) { p.write(ch); await wait(40) } }
  else if (step.keys !== undefined) p.write(step.keys)
  else if (step.wait) await wait(step.wait)
  else if (step.dump) { await wait(300); dump(step.dump) }
}
console.log('==== model requests (' + out + '/model.jsonl)\n' + fs.readFileSync(log, 'utf8'))
try { p.kill() } catch {}
await wait(500)
process.exit(0)
