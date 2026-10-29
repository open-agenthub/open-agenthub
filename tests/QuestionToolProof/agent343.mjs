// #343 e2e: a session pod's /opt/session-agent (branch or main runtime, built by build.sh) under
// proot with the runtime's own /etc/codex and /etc/opencode, the pinned CLI in the real
// common/server.js PTY, and a mock model that asks the CLI's question tool on "ASK-ME".
// usage: SA=sa343|samain node agent343.mjs <claude|codex|opencode>
import http from 'node:http'
import { spawn, execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'

const R = path.dirname(new URL(import.meta.url).pathname)
// clis/ (the pinned CLIs), proot/proot and the runtime copies live in RIG_HOME, outside the repo.
const S = process.env.RIG_HOME || path.dirname(R)
const SA = path.join(S, process.env.SA || 'sa343')
const CLIS = path.join(S, 'clis/node_modules/.bin')
const agent = process.argv[2]
const MODEL_PORT = 17100, AGENT_PORT = 17694
const out = path.join(S, 'run-' + agent); fs.rmSync(out, { recursive: true, force: true }); fs.mkdirSync(out, { recursive: true })
const log = path.join(out, 'model.log'); fs.writeFileSync(log, '')
const note = (file, value) => fs.appendFileSync(path.join(out, file), new Date().toISOString() + ' ' + value + '\n')
let replies = 0

http.createServer((req, res) => {
  let body = ''
  req.on('data', c => body += c)
  req.on('end', () => {
    fs.appendFileSync(log, `${new Date().toISOString()} ${req.method} ${req.url} ${body.length}\n`)
    let json = {}; try { json = JSON.parse(body) } catch {}
    if (req.url.includes('/models')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ object: 'list', data: [{ id: 'mock-model', object: 'model' }], models: [] })) }
    if (req.url.startsWith('/v1/messages')) {
      if (req.url.includes('count_tokens')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end('{"input_tokens":10}') }
      if (!json.stream) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify({ id: 'm', type: 'message', role: 'assistant', model: 'm', content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } })) }
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: 'm', content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
      const results = (json.messages || []).flatMap(m => Array.isArray(m.content) ? m.content : []).filter(b => b.type === 'tool_result')
      if (results.length) note('tool-results.log', JSON.stringify(results.map(r => r.content)))
      const last = (json.messages || []).filter(m => m.role === 'user').at(-1)
      const blocks = Array.isArray(last?.content) ? last.content : [{ type: 'text', text: String(last?.content || '') }]
      const lastText = JSON.stringify(blocks.filter(b => b.type === 'text'))
      const ask = (json.tools || []).some(t => t.name === 'AskUserQuestion') && blocks.at(-1)?.type === 'text' && lastText.includes('ASK-ME')
      if (ask) {
        note('asked.log', 'AskUserQuestion')
        ev('content_block_start', { index: 0, content_block: { type: 'tool_use', id: 'toolu_' + crypto.randomUUID().replace(/-/g, ''), name: 'AskUserQuestion', input: {} } })
        ev('content_block_delta', { index: 0, delta: { type: 'input_json_delta', partial_json: process.env.ASK } })
        ev('content_block_stop', { index: 0 })
        ev('message_delta', { delta: { stop_reason: 'tool_use' }, usage: { output_tokens: 1 } })
        ev('message_stop', {}); return res.end()
      }
      const text = (json.tools || []).length ? 'MOCK-REPLY-' + (++replies) : 'ok'
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } })
      ev('message_stop', {}); return res.end()
    }
    if (req.url.includes('/chat/completions')) {
      const msgs = json.messages || []
      const lastUserAt = msgs.map(m => m.role).lastIndexOf('user')
      if (msgs.some(m => m.role === 'tool')) note('tool-results.log', JSON.stringify(msgs.filter(m => m.role === 'tool').map(m => m.content)))
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const chunk = d => res.write('data: ' + JSON.stringify({ id: 'c', object: 'chat.completion.chunk', created: 1, model: 'mock-model', choices: [d] }) + '\n\n')
      if (process.env.OC_ASK && (json.tools || []).some(t => t.function?.name === 'question') && JSON.stringify(msgs[lastUserAt] || '').includes('ASK-ME') && !msgs.slice(lastUserAt).some(m => m.role === 'tool')) {
        note('asked.log', 'question')
        chunk({ index: 0, delta: { role: 'assistant', tool_calls: [{ index: 0, id: 'call_q' + Date.now(), type: 'function', function: { name: 'question', arguments: process.env.OC_ASK } }] } })
        chunk({ index: 0, delta: {}, finish_reason: 'tool_calls' })
      } else {
        chunk({ index: 0, delta: { role: 'assistant', content: json.stream === false ? 'Title' : 'MOCK-REPLY-' + (++replies) } })
        chunk({ index: 0, delta: {}, finish_reason: 'stop' })
      }
      res.write('data: [DONE]\n\n'); return res.end()
    }
    if (req.url.includes('/responses')) {
      const items = Array.isArray(json.input) ? json.input : []
      const lastUserAt = items.map(i => i.role).lastIndexOf('user')
      if (items.some(i => i.type === 'function_call_output')) note('tool-results.log', JSON.stringify(items.filter(i => i.type === 'function_call_output').map(i => i.output)))
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      const usage = { input_tokens: 1, input_tokens_details: null, output_tokens: 1, output_tokens_details: null, total_tokens: 2 }
      ev('response.created', { response: { id: 'resp_1' } })
      if (process.env.CODEX_ASK && (json.tools || []).some(t => t.name === 'request_user_input') && JSON.stringify(items[lastUserAt] || '').includes('ASK-ME') && !items.slice(lastUserAt).some(i => i.type === 'function_call_output')) {
        note('asked.log', 'request_user_input')
        ev('response.output_item.done', { output_index: 0, item: { type: 'function_call', id: 'fc_1', call_id: 'call_q' + Date.now(), name: 'request_user_input', arguments: process.env.CODEX_ASK } })
      } else {
        ev('response.output_item.done', { output_index: 0, item: { type: 'message', id: 'msg_1', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: 'MOCK-REPLY-' + (++replies), annotations: [] }] } })
      }
      ev('response.completed', { response: { id: 'resp_1', usage } })
      return res.end()
    }
    res.writeHead(404); res.end()
  })
}).listen(MODEL_PORT, '127.0.0.1')

const home = path.join(out, 'home'), work = path.join(out, 'work')
fs.mkdirSync(home, { recursive: true }); fs.mkdirSync(work, { recursive: true })
execFileSync('git', ['init', '-q', work])
const mkdir = p => fs.mkdirSync(p, { recursive: true })
let env = {
  PATH: `${CLIS}:/usr/local/bin:/usr/bin:/bin`, HOME: home, TMPDIR: '/tmp', TERM: 'xterm-256color', LANG: 'C.UTF-8',
  AGENTHUB_PORT: String(AGENT_PORT), AGENTHUB_MODE: 'interactive', AGENTHUB_UI_MODE: 'terminal', AGENTHUB_WORKDIR: work, AGENTHUB_WORKSPACE_ROOT: work,
  AGENTHUB_CALLBACK_TOKEN: 'rig-token', AGENTHUB_STATE_RESTORED: '0', AGENTHUB_SESSION_ID: 's1', AGENTHUB_SKILLS_SYNC_SECONDS: '0', AGENTHUB_RESUME: '0',
  AGENTHUB_FILE_ROOT: path.join(work, '.agenthub/files'), AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:8080/internal/sessions/s1'
}
if (agent === 'claude') {
  const key = 'sk-ant-api03-' + 'x'.repeat(80)
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ hasCompletedOnboarding: true, theme: 'dark',
    customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
    projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true } } }))
  // The hooks a Claude pod gets: the entrypoint renders them with mcp-policy-hook.sh.
  const rendered = execFileSync('bash', [path.join(SA, 'rt/claude/hooks/mcp-policy-hook.sh'), '--settings'],
    { env: { ...process.env, RUNTIME: '/opt/session-agent', AGENTHUB_RUNTIME: '/opt/session-agent/claude/hooks', AGENTHUB_MODE: 'interactive' } }).toString()
  const settings = JSON.parse(rendered)
  // Every hook payload the CLI fires, beside the pod's own hooks (does not change their answers).
  const logger = { type: 'command', command: `cat >> ${out}/hook-events.log; echo >> ${out}/hook-events.log` }
  for (const event of ['Notification', 'Stop', 'PermissionRequest', 'PostToolUse', 'UserPromptSubmit']) (settings.hooks[event] ||= []).push({ hooks: [logger] })
  if (process.env.NOTIFY_TRACE) settings.hooks.Notification[0].hooks[0].command = `bash -x ${settings.hooks.Notification[0].hooks[0].command} 2>>${out}/notify-trace.log`
  mkdir(path.join(home, '.claude')); fs.writeFileSync(path.join(home, '.claude/settings.json'), JSON.stringify(settings))
  env = { ...env, AGENTHUB_DRIVER: '/opt/session-agent/claude/driver.js', ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + MODEL_PORT, AGENTHUB_CLAUDE_SESSION_ID: crypto.randomUUID() }
} else if (agent === 'codex') {
  mkdir(path.join(home, '.codex'))
  fs.writeFileSync(path.join(home, '.codex/auth.json'), JSON.stringify({ OPENAI_API_KEY: 'sk-test' }))
  fs.writeFileSync(path.join(home, '.codex/config.toml'), `cli_auth_credentials_store = "file"\nmodel = "gpt-5.4"\nmodel_provider = "rig"\n[model_providers.rig]\nname = "rig"\nbase_url = "http://127.0.0.1:${MODEL_PORT}/v1"\nwire_api = "responses"\nenv_key = "OPENAI_API_KEY"\nsupports_websockets = false\nrequest_max_retries = 0\n[projects."${work}"]\ntrust_level = "trusted"\n`)
  env = { ...env, AGENTHUB_DRIVER: '/opt/session-agent/codex/driver.js', OPENAI_API_KEY: 'sk-test', CODEX_HOME: path.join(home, '.codex') }
} else if (agent === 'opencode') {
  mkdir(path.join(home, '.config/opencode'))
  fs.writeFileSync(path.join(home, '.config/opencode/opencode.json'), JSON.stringify({ model: 'mock/mock-model', provider: { mock: { npm: '@ai-sdk/openai-compatible', name: 'Mock',
    options: { baseURL: 'http://127.0.0.1:' + MODEL_PORT + '/v1', apiKey: 'x' }, models: { 'mock-model': { name: 'Mock model', tool_call: true } } } } }))
  env = { ...env, AGENTHUB_DRIVER: '/opt/session-agent/opencode/driver.js', OPENCODE_DISABLE_AUTOUPDATE: '1', OPENCODE_DISABLE_SHARE: '1' }
}
const tmp = fs.mkdtempSync(path.join(out, 'tmp-'))
const child = spawn(path.join(S, 'proot/proot'), ['-b', path.join(SA, 'rt') + ':/opt/session-agent', '-b', path.join(SA, 'etc-codex') + ':/etc/codex',
  '-b', path.join(SA, 'etc-opencode') + ':/etc/opencode', '-b', tmp + ':/tmp', 'node', '/opt/session-agent/server.js'], { cwd: work, env, stdio: ['ignore', 'pipe', 'pipe'] })
child.stdout.on('data', d => fs.appendFileSync(path.join(out, 'agent.log'), d))
child.stderr.on('data', d => fs.appendFileSync(path.join(out, 'agent.log'), d))
child.on('exit', code => { console.log('server.js exited', code); process.exit(0) })
console.log('agent', agent, 'runtime', SA, 'pid', child.pid)
