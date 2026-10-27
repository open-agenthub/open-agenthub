// #330 end to end: runs one session pod the backend created in the rig, as far as the pod is not
// the kubelet's business. The env is the pod spec's own (values, secretKeyRefs resolved against
// the API server, the pod IP as 127.0.0.1), with only the paths an image would provide moved into
// a scratch directory. On top of it the branch's real common/server.js starts the real Claude
// driver with the pinned Claude Code in its PTY, the real hook settings and the real in-pod MCP
// merge. A mock model stands in for the provider.
//
// The kubelet stand-in reports the pod Running and projects sessionsecrets-<id> into
// $HOME/.secrets the way a secret volume does (timestamped directory, ..data link, one link per
// key), so the agent reads the file the hub wrote and the server's masking reads it too.
//
// Run: node pod.mjs <sessionId> <scratch dir>   (env: KUBECTL, KUBECONFIG_ADMIN, REPO, CLAUDE_BIN_DIR)
import http from 'node:http'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync, spawn } from 'node:child_process'

const [sessionId, R] = process.argv.slice(2)
const REPO = process.env.REPO
const kubectl = process.env.KUBECTL || 'kubectl'
const k = (...a) => execFileSync(kubectl, ['--kubeconfig', process.env.KUBECONFIG_ADMIN, '-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
const MODEL_PORT = 17100
fs.rmSync(R, { recursive: true, force: true })
const home = path.join(R, 'home'), work = path.join(R, 'work'), SA = path.join(R, 'sa/agent-runtime')
for (const d of [home, work, path.join(home, '.claude'), path.join(R, 'tmp')]) fs.mkdirSync(d, { recursive: true })
const log = (file, line) => fs.appendFileSync(path.join(R, file), `${new Date().toISOString()} ${line}\n`)

// The branch's runtime, laid out as in the image, with node_modules (incl. the built node-pty) from
// the image's session agent.
fs.cpSync(path.join(REPO, 'agent-runtime'), SA, { recursive: true, filter: src => path.basename(src) !== 'node_modules' })
fs.symlinkSync('/opt/session-agent/node_modules', path.join(SA, 'node_modules'))
fs.symlinkSync('/opt/session-agent/node_modules', path.join(SA, 'session-agent/node_modules'))

// ---------------------------------------------------------------- mock model
// Turn 1 (the session's prompt): ask for the secret through the in-pod MCP. Turn 2 (its result):
// read the file with Bash. Turn 3: repeat what the file said, so the value is in the
// transcript three times — tool input never, tool result once, assistant text once.
const SECRET_FILE = 'deploy-key.json'
http.createServer((req, res) => {
  let body = ''
  req.on('data', c => body += c)
  req.on('end', () => {
    let json = {}; try { json = JSON.parse(body) } catch {}
    if (!req.url.startsWith('/v1/messages')) { res.writeHead(404); return res.end() }
    if (req.url.includes('count_tokens')) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end('{"input_tokens":10}') }
    const blocks = (json.messages || []).flatMap(m => Array.isArray(m.content) ? m.content : [])
    const uses = Object.fromEntries(blocks.filter(b => b.type === 'tool_use').map(b => [b.id, b.name]))
    const results = blocks.filter(b => b.type === 'tool_result').map(b => ({ name: uses[b.tool_use_id],
      text: typeof b.content === 'string' ? b.content : (b.content || []).map(c => c.text || '').join('') }))
    const asking = String(body).includes('NEED-SECRET')
    if (!json.stream || !asking) {
      // Title generation and other side requests.
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ id: 'm', type: 'message', role: 'assistant', model: 'm', content: [{ type: 'text', text: 'Deploy key' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } }))
    }
    for (const r of results) log('model.log', `tool_result ${r.name}: ${r.text.slice(0, 400)}`)
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
    ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: 'm', content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
    const tool = (name, input) => {
      log('model.log', `tool_use ${name} ${JSON.stringify(input)}`)
      ev('content_block_start', { index: 0, content_block: { type: 'tool_use', id: 'toolu_' + crypto.randomUUID().replace(/-/g, ''), name, input: {} } })
      ev('content_block_delta', { index: 0, delta: { type: 'input_json_delta', partial_json: JSON.stringify(input) } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'tool_use' }, usage: { output_tokens: 1 } })
    }
    // Claude Code 2.1.287 moves an MCP call still running after 120 s into the background and
    // hands its result over later as a task notification, so "provided" may arrive in either.
    const askedBefore = blocks.some(b => b.type === 'tool_use' && b.name === 'mcp__agenthub_sessions__secret_request')
    const provided = /status\\?"\s*:\s*\\?"provided/.test(body)
    const read = results.find(r => r.name === 'Bash')
    if (!askedBefore) tool('mcp__agenthub_sessions__secret_request', { name: SECRET_FILE, delivery: 'file', purpose: 'deploy to staging', waitMinutes: 10 })
    else if (!read && provided) {
      const where = results.some(r => r.name === 'mcp__agenthub_sessions__secret_request' && /provided/.test(r.text)) ? 'tool result' : 'task notification'
      log('model.log', `saw provided in the ${where}`)
      tool('Bash', { command: `cat ~/.secrets/${SECRET_FILE}`, description: 'Read the deploy key' })
    } else {
      const text = read ? `The deploy key file holds: ${read.text.trim()}` : 'Waiting for the owner to provide the key.'
      log('model.log', `${read ? 'final' : 'waiting'} ${text}`)
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } })
    }
    ev('message_stop', {})
    res.end()
  })
}).listen(MODEL_PORT, '127.0.0.1')

// ---------------------------------------------------------------- the pod's env
const pod = JSON.parse(k('get', 'pod', `session-${sessionId}`, '-o', 'json'))
const container = pod.spec.containers.find(c => c.name === 'agent') || pod.spec.containers[0]
const secretData = name => { try { return JSON.parse(k('get', 'secret', name, '-o', 'json')).data || {} } catch { return {} } }
const env = {}
for (const e of container.env || []) {
  if (e.value !== undefined) env[e.name] = e.value
  else if (e.valueFrom?.secretKeyRef) {
    const v = secretData(e.valueFrom.secretKeyRef.name)[e.valueFrom.secretKeyRef.key]
    if (v !== undefined) env[e.name] = Buffer.from(v, 'base64').toString('utf8')
  } else if (e.valueFrom?.fieldRef?.fieldPath === 'status.podIP') env[e.name] = '127.0.0.1'
}
const remap = v => typeof v === 'string' ? v.replaceAll('/home/agent', home).replace(/^\/workspace/, work) : v
for (const key of Object.keys(env)) env[key] = remap(env[key])
// The mcp volume, as the kubelet would mount it, then the entrypoint's merge of the built-in server.
const mcpDir = path.join(R, 'secrets-mcp'); fs.mkdirSync(mcpDir)
for (const [key, b64] of Object.entries(secretData(`mcp-${sessionId}`))) fs.writeFileSync(path.join(mcpDir, key), Buffer.from(b64, 'base64'))
const mergedMcp = path.join(R, 'tmp/agenthub-mcp.json')
const runtimeEnv = { ...process.env, RUNTIME: SA }
execFileSync(process.execPath, [path.join(SA, 'sessions/configure.mjs'), path.join(mcpDir, 'mcp.json'), mergedMcp], { env: runtimeEnv })
// Claude's own setup steps from claude/entrypoint.sh, against this HOME.
const claudeJson = path.join(home, '.claude.json')
execFileSync(process.execPath, [path.join(SA, 'claude/workspace-trust.mjs'), claudeJson, work])
execFileSync(process.execPath, [path.join(SA, 'claude/mcp-config.mjs'), claudeJson, mergedMcp])
const settings = execFileSync(path.join(SA, 'claude/hooks/mcp-policy-hook.sh'), ['--settings'], { env: { ...runtimeEnv, AGENTHUB_RUNTIME: path.join(SA, 'claude/hooks') }, encoding: 'utf8' })
fs.writeFileSync(path.join(home, '.claude/settings.json'), settings)
// No provider login in the rig: a fake key the mock model accepts, pre-approved like a stored one.
const key = env.ANTHROPIC_API_KEY || 'sk-ant-api03-' + 'x'.repeat(80)
const cfg = JSON.parse(fs.readFileSync(claudeJson, 'utf8'))
Object.assign(cfg, { hasCompletedOnboarding: true, theme: 'dark', customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] } })
fs.writeFileSync(claudeJson, JSON.stringify(cfg))

const base = Object.fromEntries(Object.entries(process.env).filter(([n]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OTEL)/.test(n)))
const agentEnv = {
  ...base, ...env, HOME: home, TERM: 'xterm-256color', COLORTERM: 'truecolor', TMPDIR: path.join(R, 'tmp'),
  PATH: `${process.env.CLAUDE_BIN_DIR}:${process.env.PATH}`, RUNTIME: SA,
  ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: `http://127.0.0.1:${MODEL_PORT}`,
  AGENTHUB_MCP_CONFIG: mergedMcp, AGENTHUB_DRIVER: path.join(SA, 'claude/driver.js'),
  AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(R, 'tmp/watcher.pid'),
  // Telemetry would go to the rig backend's OTLP endpoint; it is not what this proves.
  CLAUDE_CODE_ENABLE_TELEMETRY: '0'
}
fs.writeFileSync(path.join(R, 'env-names.txt'), Object.keys(env).sort().join('\n') + '\n')

// ---------------------------------------------------------------- kubelet stand-in
k('patch', 'pod', `session-${sessionId}`, '--subresource=status', '--type=merge', '-p',
  JSON.stringify({ status: { phase: 'Running', podIP: '127.0.0.1', podIPs: [{ ip: '127.0.0.1' }] } }))
const secretsDir = env.AGENTHUB_SECRETS_DIR
fs.mkdirSync(secretsDir, { recursive: true })
let projected = ''
setInterval(() => {
  const data = secretData(`sessionsecrets-${sessionId}`)
  const digest = JSON.stringify(data)
  if (digest === projected) return
  projected = digest
  const stamp = '..' + new Date().toISOString().replace(/[:.]/g, '_')
  fs.mkdirSync(path.join(secretsDir, stamp))
  for (const [name, b64] of Object.entries(data)) fs.writeFileSync(path.join(secretsDir, stamp, name), Buffer.from(b64, 'base64'), { mode: 0o640 })
  fs.symlinkSync(stamp, path.join(secretsDir, '..data_tmp'))
  fs.renameSync(path.join(secretsDir, '..data_tmp'), path.join(secretsDir, '..data'))
  for (const name of Object.keys(data)) if (!fs.existsSync(path.join(secretsDir, name))) fs.symlinkSync(path.join('..data', name), path.join(secretsDir, name))
  log('kubelet.log', `projected ${Object.keys(data).join(',') || '(none)'}`)
}, 1000)

const child = spawn(process.execPath, [path.join(SA, 'common/server.js')], { cwd: work, env: agentEnv, stdio: ['ignore', 'pipe', 'pipe'] })
child.stdout.on('data', d => fs.appendFileSync(path.join(R, 'agent.log'), d))
child.stderr.on('data', d => fs.appendFileSync(path.join(R, 'agent.log'), d))
child.on('exit', code => { log('agent.log', `server.js exited ${code}`); process.exit(0) })
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => { child.kill('SIGTERM'); setTimeout(() => process.exit(0), 3000) })
console.log(`pod ${sessionId} running from ${R}, agent port ${env.AGENTHUB_PORT}`)
