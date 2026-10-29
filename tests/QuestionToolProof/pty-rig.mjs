// Real agent CLI in a real PTY (node-pty from the session agent), its output rendered by
// @xterm/headless 5.5 (the frontend's xterm). The headless terminal answers the CLI's terminal
// queries, as the browser's xterm does. A mock model records what reaches it.
// usage: node pty-rig.mjs <agent> '<steps json>' [outdir]
// steps: {"paste":"/model"} composer path (bracketed paste, 150 ms, CR) | {"type":"/mo"} key by key
//        {"keys":"\u001b"} | {"wait":ms} | {"dump":"name"} | {"ready":"regex"}
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { createRequire } from 'node:module'
const require = createRequire(import.meta.url)
const pty = require('/opt/session-agent/node_modules/node-pty')
const { Terminal } = require('@xterm/headless')

const R = path.dirname(new URL(import.meta.url).pathname)
// clis/ (the pinned CLIs) and every run's output live in RIG_HOME, outside the repo.
const H = process.env.RIG_HOME || path.join(R, '..')
const CLIS = path.join(H, 'clis/node_modules/.bin')
const CURSOR = path.join(H, 'cursorhome/.local/bin/cursor-agent')
const [agent, stepsJson, outName = 'screens-' + agent] = process.argv.slice(2)
const steps = JSON.parse(stepsJson || '[]')
const out = path.resolve(H, outName); fs.mkdirSync(out, { recursive: true })
const PORT = 17000 + Math.floor(Math.random() * 900)
const log = path.join(out, 'model.log'); fs.writeFileSync(log, '')

http.createServer((req, res) => {
  let body = ''
  req.on('data', c => body += c)
  req.on('end', () => {
    fs.appendFileSync(log, req.method + ' ' + req.url + ' ' + body.slice(-1500) + '\n\n')
    let json = {}; try { json = JSON.parse(body) } catch {}
    if (req.url.includes('/models')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ object: 'list', data: [{ id: 'mock-model', object: 'model' }], models: [] })) }
    if (req.url.startsWith('/v1/messages')) {
      if (!json.stream) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ id: 'm', type: 'message', role: 'assistant', model: 'm', content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } })) }
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: 'm', content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
      const ask = (json.tools || []).some(t => t.name === 'AskUserQuestion') && !JSON.stringify(json.messages || []).includes('tool_result')
      if (ask && process.env.ASK) {
        ev('content_block_start', { index: 0, content_block: { type: 'tool_use', id: 'toolu_' + crypto.randomUUID().replace(/-/g, ''), name: 'AskUserQuestion', input: {} } })
        ev('content_block_delta', { index: 0, delta: { type: 'input_json_delta', partial_json: process.env.ASK } })
        ev('content_block_stop', { index: 0 })
        ev('message_delta', { delta: { stop_reason: 'tool_use' }, usage: { output_tokens: 1 } })
        ev('message_stop', {}); return res.end()
      }
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'MOCK-REPLY' } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } })
      ev('message_stop', {}); return res.end()
    }
    if (req.url.includes('/chat/completions')) {
      const msgs = json.messages || []
      if (process.env.OC_ASK && (json.tools || []).some(t => t.function?.name === 'question') && !msgs.some(m => m.role === 'tool') && JSON.stringify(msgs).includes('ASK-ME')) {
        res.writeHead(200, { 'content-type': 'text/event-stream' })
        const chunk = d => res.write('data: ' + JSON.stringify({ id: 'c', object: 'chat.completion.chunk', created: 1, model: 'mock-model', choices: [d] }) + '\n\n')
        chunk({ index: 0, delta: { role: 'assistant', tool_calls: [{ index: 0, id: 'call_q' + Date.now(), type: 'function', function: { name: 'question', arguments: process.env.OC_ASK } }] } })
        chunk({ index: 0, delta: {}, finish_reason: 'tool_calls' })
        res.write('data: [DONE]\n\n'); return res.end()
      }
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const chunk = d => res.write('data: ' + JSON.stringify({ id: 'c', object: 'chat.completion.chunk', created: 1, model: 'mock-model', choices: [d] }) + '\n\n')
      chunk({ index: 0, delta: { role: 'assistant', content: 'MOCK-REPLY' } })
      chunk({ index: 0, delta: {}, finish_reason: 'stop' })
      res.write('data: [DONE]\n\n'); return res.end()
    }
    if (req.url.includes('/responses')) {
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      const items = Array.isArray(json.input) ? json.input : []
      if (process.env.CODEX_ASK && (json.tools || []).some(t => t.name === 'request_user_input') && JSON.stringify(items).includes('ASK-ME') && !items.some(i => i.type === 'function_call_output')) {
        ev('response.created', { response: { id: 'resp_q' } })
        ev('response.output_item.done', { output_index: 0, item: { type: 'function_call', id: 'fc_1', call_id: 'call_q1', name: 'request_user_input', arguments: process.env.CODEX_ASK } })
        ev('response.completed', { response: { id: 'resp_q', usage: { input_tokens: 1, input_tokens_details: null, output_tokens: 1, output_tokens_details: null, total_tokens: 2 } } })
        return res.end()
      }
      const item = { type: 'message', id: 'msg_1', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'MOCK-REPLY', annotations: [] }] }
      ev('response.created', { response: { id: 'resp_1' } })
      ev('response.output_item.done', { output_index: 0, item })
      ev('response.completed', { response: { id: 'resp_1', usage: { input_tokens: 1, input_tokens_details: null, output_tokens: 1, output_tokens_details: null, total_tokens: 2 } } })
      return res.end()
    }
    res.writeHead(404); res.end()
  })
}).listen(PORT, '127.0.0.1')

const home = fs.mkdtempSync(path.join(H, 'home-' + agent + '-'))
const work = fs.mkdtempSync(path.join(H, 'work-' + agent + '-'))
const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW)/.test(k)))
let env = { ...base, HOME: home, TERM: 'xterm-256color', COLORTERM: 'truecolor', PATH: CLIS + ':' + process.env.PATH }
let cmd, args = []
const mkdir = p => fs.mkdirSync(p, { recursive: true })
if (agent === 'claude') {
  const key = 'sk-ant-api03-' + 'x'.repeat(80)
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ hasCompletedOnboarding: true, theme: 'dark',
    customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
    projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true } } }))
  if (process.env.WITH_CUSTOM) {
    mkdir(path.join(home, '.claude/commands')); fs.writeFileSync(path.join(home, '.claude/commands/deploy-check.md'), '---\ndescription: Check the deploy\nargument-hint: <env>\n---\nCheck the deploy of $ARGUMENTS')
    mkdir(path.join(home, '.claude/skills/rig-skill')); fs.writeFileSync(path.join(home, '.claude/skills/rig-skill/SKILL.md'), '---\nname: rig-skill\ndescription: A rig skill\n---\nSay RIG-SKILL')
  }
  env = { ...env, ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + PORT }
  if (process.env.HOOKLOG) {
    mkdir(path.join(home, '.claude'))
    const h = [{ hooks: [{ type: 'command', command: process.env.HOOKLOG }] }]
    fs.writeFileSync(path.join(home, '.claude/settings.json'), JSON.stringify({ hooks: Object.fromEntries(['PreToolUse', 'PostToolUse', 'PermissionRequest', 'Notification', 'UserPromptSubmit', 'Stop'].map(e => [e, h])) }))
  }
  cmd = path.join(CLIS, 'claude'); args = ['--session-id', crypto.randomUUID()]
} else if (agent === 'codex') {
  mkdir(path.join(home, '.codex'))
  fs.writeFileSync(path.join(home, '.codex/auth.json'), JSON.stringify({ OPENAI_API_KEY: 'sk-test' }))
  const hk = process.env.HOOKLOG ? ['PreToolUse', 'PostToolUse', 'PermissionRequest', 'UserPromptSubmit', 'Stop'].map(e => `[[hooks.${e}]]\n${e === 'Stop' || e === 'UserPromptSubmit' ? '' : 'matcher = "*"\n'}[[hooks.${e}.hooks]]\ntype = "command"\ncommand = "${process.env.HOOKLOG}"\ntimeout = 10\n`).join('\n') : ''
  fs.writeFileSync(path.join(home, '.codex/config.toml'), `check_for_update_on_startup = false\ncli_auth_credentials_store = "file"\nmodel = "gpt-5.4"\nmodel_provider = "rig"\n[model_providers.rig]\nname = "rig"\nbase_url = "http://127.0.0.1:${PORT}/v1"\nwire_api = "responses"\nsupports_websockets = false\nenv_key = "OPENAI_API_KEY"\n[features]\nhooks = true\n[projects."${work}"]\ntrust_level = "trusted"\n${hk}`)
  if (process.env.WITH_CUSTOM) {
    mkdir(path.join(home, '.codex/prompts')); fs.writeFileSync(path.join(home, '.codex/prompts/deploy-check.md'), '---\ndescription: Check the deploy\nargument-hint: ENV=<env>\n---\nCheck the deploy of $ENV')
    mkdir(path.join(home, '.agents/skills/rig-skill')); fs.writeFileSync(path.join(home, '.agents/skills/rig-skill/SKILL.md'), '---\nname: rig-skill\ndescription: A rig skill\n---\nSay RIG-SKILL')
  }
  env = { ...env, OPENAI_API_KEY: 'sk-test' }
  cmd = path.join(CLIS, 'codex'); args = ['--no-alt-screen', '--no-daemon']
} else if (agent === 'opencode') {
  mkdir(path.join(home, '.config/opencode'))
  fs.writeFileSync(path.join(home, '.config/opencode/opencode.json'), JSON.stringify({ $schema: 'https://opencode.ai/config.json', autoupdate: false, share: 'disabled',
    ...(process.env.OC_PLUGIN ? { plugin: ['file://' + process.env.OC_PLUGIN] } : {}), model: 'mock/mock-model', provider: { mock: { npm: '@ai-sdk/openai-compatible', name: 'Mock', options: { baseURL: 'http://127.0.0.1:' + PORT + '/v1', apiKey: 'x' }, models: { 'mock-model': { name: 'Mock model' } } } },
    ...(process.env.WITH_CUSTOM ? { command: { 'deploy-check': { template: 'Check the deploy of $ARGUMENTS', description: 'Check the deploy' } } } : {}) }))
  cmd = path.join(CLIS, 'opencode'); args = []
} else if (agent === 'cursor') {
  env = { ...env, CURSOR_API_KEY: process.env.RIG_CURSOR_KEY || 'key_test' }
  cmd = CURSOR; args = ['--trust', '--disable-auto-update']
} else if (agent === 'openclaw') {
  env = { ...env, ANTHROPIC_API_KEY: 'sk-ant-test' }
  cmd = path.join(CLIS, 'openclaw'); args = ['tui', '--local']
}
fs.writeFileSync(path.join(out, 'home'), home)
if (process.env.PROOT) { args = [...process.env.PROOT_BINDS.split(' ').flatMap(b => ['-b', b]), cmd, ...args]; cmd = process.env.PROOT }

const cols = Number(process.env.COLS || 120), rows = Number(process.env.ROWS || 40)
const term = new Terminal({ cols, rows, allowProposedApi: true })
const p = pty.spawn(cmd, args, { name: 'xterm-256color', cols, rows, cwd: work, env })
p.onData(d => term.write(d))
term.onData(d => p.write(d)) // terminal query answers, as xterm.js in the browser sends them
let exited = null
p.onExit(e => { exited = e })
const wait = ms => new Promise(r => setTimeout(r, ms))
const screen = () => {
  const b = term.buffer.active, lines = []
  for (let y = 0; y < term.rows; y++) lines.push(b.getLine(b.viewportY + y)?.translateToString(true) ?? '')
  return lines.join('\n')
}
const dump = name => {
  fs.writeFileSync(path.join(out, name + '.txt'), screen())
  console.log('==== ' + name + (exited ? ' (exited ' + JSON.stringify(exited) + ')' : '') + '\n' + screen().split('\n').filter(l => l.trim()).join('\n'))
}
for (const step of steps) {
  if (step.ready) { const end = Date.now() + (step.ms || 90000); while (!new RegExp(step.ready).test(screen()) && Date.now() < end && !exited) await wait(250); await wait(1500) }
  else if (step.paste !== undefined) { p.write('\x1b[200~' + step.paste + '\x1b[201~'); await wait(150); p.write('\r') }
  else if (step.pasteOnly !== undefined) { p.write('\x1b[200~' + step.pasteOnly + '\x1b[201~') }
  else if (step.type !== undefined) { for (const ch of step.type) { p.write(ch); await wait(40) } }
  else if (step.keys !== undefined) p.write(step.keys)
  else if (step.wait) await wait(step.wait)
  else if (step.scan) { const seen = new Set(); const all = []; for (let i = 0; i < step.scan; i++) { for (const l of screen().split('\n')) if (l.trim() && !seen.has(l.trim())) { seen.add(l.trim()); all.push(l) } p.write(step.key || '\x1b[B'); await wait(step.gap || 120) } fs.writeFileSync(path.join(out, 'scan.txt'), all.join('\n')); console.log('==== scan ' + all.length) }
  else if (step.dump) { await wait(300); dump(step.dump) }
}
const reqs = fs.readFileSync(log, 'utf8')
console.log('model log bytes:', reqs.length, '; MOCK-REPLY requests:', (reqs.match(/^POST/gm) || []).length)
try { p.kill() } catch {}
await wait(500); process.exit(0)
