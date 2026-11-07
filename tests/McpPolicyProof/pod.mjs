// #514 end to end: runs one session pod the backend created in the rig, as far as the pod is not
// the kubelet's business. The env is the pod spec's own (values, secretKeyRefs resolved against
// the API server, the pod IP as 127.0.0.1), with only the paths an image would provide moved into
// a scratch directory. On top of it the branch's real common/server.js starts the real Claude
// driver with the pinned Claude Code in its PTY, the real hook settings (mcp-policy-hook.sh and
// pretooluse-hook.sh) and the real MCP config merge. A mock model stands in for the provider.
//
// The model calls, one after the other, one MCP tool per PLAN entry and then ends its turn. An
// entry is a pattern matched against the tool names the CLI offers, because the CLI decides how a
// server or tool name with "." or "-" is spelled; with no offered match the entry's literal text
// is called. model.log gets the offered names, each resolved call and every tool result.
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
const PLAN = (process.env.PLAN || '^mcp__docs__search$,^mcp__docs__read$,^mcp__team-docs__search$,^mcp__docs__get.data.v2$,^mcp__ops.tools__search$').split(',')
let resolved = null
const loggedResults = new Set()
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
    // Side requests (title, suggestions) carry no tools; only the agent's own turn does.
    if (!json.stream || !String(body).includes('MCP-PROOF') || !(json.tools || []).length) {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ id: 'm', type: 'message', role: 'assistant', model: 'm', content: [{ type: 'text', text: 'MCP proof' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } }))
    }
    // Claude Code 2.1.318 defers MCP tools: they are not in `tools` until the model looks them up
    // with ToolSearch, as a real model does. The names are taken from that lookup's result, so the
    // calls use the CLI's own spelling of servers and tools with "." or "-" in them.
    const searched = results.some(r => r.name === 'ToolSearch')
    if (!searched && (json.tools || []).some(t => t.name === 'ToolSearch')) {
      log('model.log', `tools ${(json.tools || []).map(t => t.name).join(' ')}`)
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: 'm', content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
      ev('content_block_start', { index: 0, content_block: { type: 'tool_use', id: 'toolu_' + crypto.randomUUID().replace(/-/g, ''), name: 'ToolSearch', input: {} } })
      ev('content_block_delta', { index: 0, delta: { type: 'input_json_delta', partial_json: JSON.stringify({ query: 'docs search read get data ops tools team', max_results: 20 }) } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'tool_use' }, usage: { output_tokens: 1 } })
      ev('message_stop', {})
      return res.end()
    }
    if (!resolved) {
      const offered = [...new Set([...(json.tools || []).map(t => t.name), ...(String(body).match(/mcp__[A-Za-z0-9_.-]+/g) || [])])]
        .filter(n => /^mcp__(docs|team|ops)/.test(n))
      log('model.log', `offered ${offered.join(' ')}`)
      resolved = PLAN.map(entry => offered.find(n => new RegExp(entry).test(n)) || entry.replace(/^\^|\$$/g, ''))
      for (const [i, entry] of PLAN.entries()) log('model.log', `plan ${entry} -> ${resolved[i]}`)
    }
    const done = new Set(results.map(r => r.name))
    for (const r of results) if (!loggedResults.has(r.name) && loggedResults.add(r.name)) log('model.log', `tool_result ${r.name}: ${r.text.replace(/\s+/g, ' ').slice(0, 300)}`)
    res.writeHead(200, { 'content-type': 'text/event-stream' })
    const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
    ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: 'm', content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
    const next = (resolved || []).find(name => !done.has(name))
    if (next) {
      log('model.log', `tool_use ${next}`)
      ev('content_block_start', { index: 0, content_block: { type: 'tool_use', id: 'toolu_' + crypto.randomUUID().replace(/-/g, ''), name: next, input: {} } })
      ev('content_block_delta', { index: 0, delta: { type: 'input_json_delta', partial_json: JSON.stringify({ query: 'proof' }) } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'tool_use' }, usage: { output_tokens: 1 } })
    } else {
      log('model.log', 'final')
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'MCP proof done.' } })
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

const child = spawn(process.execPath, [path.join(SA, 'common/server.js')], { cwd: work, env: agentEnv, stdio: ['ignore', 'pipe', 'pipe'] })
child.stdout.on('data', d => fs.appendFileSync(path.join(R, 'agent.log'), d))
child.stderr.on('data', d => fs.appendFileSync(path.join(R, 'agent.log'), d))
child.on('exit', code => { log('agent.log', `server.js exited ${code}`); process.exit(0) })
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => { child.kill('SIGTERM'); setTimeout(() => process.exit(0), 3000) })
console.log(`pod ${sessionId} running from ${R}, agent port ${env.AGENTHUB_PORT}`)
