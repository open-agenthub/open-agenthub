// Built-in session tools in Codex sessions (#504), against the real backend's decisions.
//
// For each case the script creates a Codex session through the real backend (dev auth, real
// kube-apiserver and Postgres), reads the pod spec the backend wrote, and runs the pinned Codex
// CLI here with that pod's environment: its callback URL and token, mode and flags. Codex runs
// with the driver's own arguments, the runtime's real policy hook and the real `agenthub_sessions`
// MCP server, configured by the runtime's own `mcp-config.js --builtin`. A mock model calls the
// built-in tools; the script records what each call returned.
//
// The managed requirements cannot be bound over /etc/codex here (no working proot in the pod),
// so the same hook entries are loaded from CODEX_HOME/config.toml. The script checks that the
// hook actually ran for every call, so a hook that silently did not load cannot pass as "allowed".
//
// Env: API (backend, default http://127.0.0.1:8080), KUBECONFIG, KUBECTL, CODEX_BIN, OUT,
// RUNTIME (a directory holding the runtime layout, see README), CASES (comma list, default all).
import { createRequire } from 'node:module'
import { execFileSync, spawn } from 'node:child_process'
import { createServer } from 'node:http'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
const require = createRequire(import.meta.url)
const here = path.dirname(fileURLToPath(import.meta.url))
const repo = path.resolve(here, '../..')
const driver = require(path.join(repo, 'agent-runtime/codex/driver.js'))

const API = process.env.API || 'http://127.0.0.1:8080'
const OUT = process.env.OUT || fs.mkdtempSync(path.join(os.tmpdir(), 'codex-builtin-'))
const RUNTIME = process.env.RUNTIME
const CODEX = process.env.CODEX_BIN
if (!RUNTIME || !CODEX) throw new Error('RUNTIME and CODEX_BIN are required')
const pin = /ARG CODEX_VERSION=(\S+)/.exec(fs.readFileSync(path.join(repo, 'agent-runtime/codex/Dockerfile'), 'utf8'))[1]
const version = execFileSync(CODEX, ['--version'], { encoding: 'utf8' }).trim()
if (version !== `codex-cli ${pin}`) throw new Error(`pinned ${pin}, got ${version}`)

const kubectl = (...args) => execFileSync(process.env.KUBECTL || 'kubectl',
  ['--kubeconfig', process.env.KUBECONFIG, '-n', 'agenthub-sessions', ...args], { encoding: 'utf8' })
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))
async function api(method, p, body) {
  const response = await fetch(API + '/api' + p, { method, headers: { 'content-type': 'application/json' }, body: body && JSON.stringify(body) })
  return { status: response.status, body: await response.json().catch(() => null) }
}

// What a parent agent or an owner would send. `policy` is the session_create MCP tool's field.
const ALL_CASES = {
  'autonomous (default auto-approve)': { mode: 'Autonomous' },
  'autonomous, auto-approve off': { mode: 'Autonomous', autoApprove: false },
  'autonomous, auto-approve off, allow list names container_run': {
    mode: 'Autonomous', autoApprove: false,
    policy: { allowedMcpTools: ['mcp__agenthub_sessions__container_run'] } },
  'autonomous, auto-approve off, legacy allowedTools': {
    mode: 'Autonomous', autoApprove: false, allowedTools: ['mcp__agenthub_sessions__container_run'] },
  'interactive, auto-approve on': { mode: 'Interactive', autoApprove: true },
  'interactive, auto-approve on, allow list names container_run': {
    mode: 'Interactive', autoApprove: true, policy: { allowedMcpTools: ['mcp__agenthub_sessions__container_run'] } }
}
const wanted = (process.env.CASES || '').split(',').map(s => s.trim()).filter(Boolean)
const CASES = Object.fromEntries(Object.entries(ALL_CASES).filter(([label], i) => !wanted.length || wanted.includes(String(i + 1))))

// The built-in tools under test, in call order. container_run needs the containers feature;
// with it off the tool is not registered, so the call shows whether the policy got that far.
const CALLS = [
  { tool: 'agent_inbox', args: {} },
  { tool: 'container_run', args: { name: 'helper', image: 'registry.example.com/helper:1' } },
  { tool: 'container_list', args: {} },
  { tool: 'container_stop', args: { name: 'helper' } }
]

function event(type, data) { return `event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n` }
function stream(items, id) {
  return event('response.created', { response: { id } }) +
    items.map(item => event('response.output_item.done', { item })).join('') +
    event('response.completed', { response: { id, usage: { input_tokens: 0, output_tokens: 0, total_tokens: 0,
      input_tokens_details: null, output_tokens_details: null } } })
}
// Codex lists MCP tools to the model either flat or inside a namespace entry, depending on the
// version; find the name it will accept for one tool.
function toolName(tools, tool) {
  const flat = []
  for (const t of tools || []) {
    if (t.name) flat.push({ name: t.name, ns: null })
    for (const inner of t.tools || []) flat.push({ name: inner.name, ns: t.name })
  }
  const hit = flat.find(t => t.name === tool && t.ns && /agenthub_sessions/.test(t.ns))
    || flat.find(t => t.name.endsWith(tool) && /agenthub_sessions/.test(t.name))
  return hit ? hit : null
}

async function runCase(label, body) {
  const created = await api('POST', '/sessions', { title: `#504 ${label}`, agent: 'Codex', authMode: 'ApiKey', prompt: 'x', ...body })
  if (created.status >= 300) throw new Error(`create ${label}: ${created.status} ${JSON.stringify(created.body)}`)
  const id = created.body.id
  let pod
  for (let i = 0; i < 50 && !pod; i++) {
    const json = kubectl('get', 'pod', `session-${id}`, '-o', 'json', '--ignore-not-found')
    if (json.trim()) pod = JSON.parse(json); else await sleep(200)
  }
  const podEnv = {}
  for (const e of pod.spec.containers.find(c => c.name === 'agent').env) if (e.value !== undefined) podEnv[e.name] = e.value

  const work = path.join(OUT, id)
  const home = path.join(work, 'home')
  fs.mkdirSync(home, { recursive: true })
  execFileSync('git', ['init', '-q', work])
  const hookLog = path.join(work, 'hook.jsonl')
  // The hook as requirements.toml runs it, plus a recorder in front that logs the event and
  // what the real hook answered.
  const recorder = path.join(work, 'hook-recorder.mjs')
  fs.writeFileSync(recorder, `import { spawnSync } from 'node:child_process'; import fs from 'node:fs'
let input = ''; for await (const c of process.stdin) input += c
const r = spawnSync(process.execPath, [${JSON.stringify(path.join(RUNTIME, 'codex/policy-hook.js'))}], { input, encoding: 'utf8', env: process.env })
const p = JSON.parse(input)
fs.appendFileSync(${JSON.stringify(hookLog)}, JSON.stringify({ event: p.hook_event_name, tool: p.tool_name, output: r.stdout }) + '\\n')
process.stdout.write(r.stdout)
`)
  // The two policy-hook entries of requirements.toml (PreToolUse, PermissionRequest), with the
  // recorder in front; the matcher there is "*" for both.
  const hookToml = []
  for (const eventName of ['PreToolUse', 'PermissionRequest']) {
    hookToml.push(`[[hooks.${eventName}]]\nmatcher = "*"\n\n[[hooks.${eventName}.hooks]]\ntype = "command"\ncommand = ${JSON.stringify(`${process.execPath} ${recorder}`)}\ntimeout = 60\n`)
  }
  const builtin = execFileSync(process.execPath, [path.join(RUNTIME, 'codex/mcp-config.js'), '--builtin'],
    { encoding: 'utf8', env: { ...podEnv, RUNTIME, PATH: process.env.PATH } })
  fs.writeFileSync(path.join(home, 'config.toml'), [
    'cli_auth_credentials_store = "file"',
    `[projects.${JSON.stringify(work)}]`, 'trust_level = "trusted"', '',
    '[features]', 'hooks = true', '',
    ...hookToml, builtin
  ].join('\n'))
  fs.writeFileSync(path.join(home, 'auth.json'), JSON.stringify({ auth_mode: 'apikey', OPENAI_API_KEY: 'synthetic-key' }))

  const requests = []
  let step = 0
  let searched = false
  const server = createServer((req, res) => {
    let raw = ''
    req.on('data', c => { raw += c })
    req.on('end', () => {
      if (req.url !== '/v1/responses') return res.writeHead(404).end()
      const payload = JSON.parse(raw)
      // The title request has no tools; answer it without spending a step.
      requests.push(payload)
      fs.appendFileSync(path.join(work, 'model-requests.jsonl'), JSON.stringify({ tools: (payload.tools || []).map(t => ({ type: t.type, name: t.name, inner: (t.tools || []).map(i => i.name) })), input: (payload.input || []).slice(-3) }) + '\n')
      res.writeHead(200, { 'Content-Type': 'text/event-stream' })
      if (!payload.tools?.length) return res.end(stream([{ type: 'message', role: 'assistant', id: 't', content: [{ type: 'output_text', text: 'title' }] }], 'title'))
      // Codex 0.167 defers MCP tools behind tool_search: search once, then the tools come back
      // in a tool_search_output item of the next request's input.
      const discovered = (payload.input || []).filter(i => i.type === 'tool_search_output').flatMap(i => i.tools || [])
      if (!searched) {
        searched = true
        return res.end(stream([{ type: 'tool_search_call', call_id: 'search-1', status: 'completed', execution: 'client',
          arguments: { query: 'agenthub_sessions agent_inbox container_run container_list container_stop', limit: 20 } }], 'search'))
      }
      const call = CALLS[step++]
      if (!call) return res.end(stream([{ type: 'message', role: 'assistant', id: 'done', content: [{ type: 'output_text', text: 'done' }] }], `r${step}`))
      const found = toolName([...(payload.tools || []), ...discovered], call.tool)
      if (!found) return res.end(stream([{ type: 'message', role: 'assistant', id: 'missing', content: [{ type: 'output_text', text: `tool ${call.tool} not offered` }] }], `r${step}`))
      const item = { type: 'function_call', call_id: `call-${call.tool}`, name: found.name, arguments: JSON.stringify(call.args) }
      if (found.ns) item.namespace = found.ns
      res.end(stream([item], `r${step}`))
    })
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const base = `http://127.0.0.1:${server.address().port}`
  // exec for every mode: the backend decides from the session record, not from the CLI's mode. An
  // interactive pod runs the TUI, which this script does not drive; its hook path is the same.
  const built = driver.buildCommand({ ...podEnv, AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'Use the session tools.' }, false)
  const args = [...built.args]
  const at = args.findIndex(a => a === 'exec') + 1 || 0
  args.splice(at, 0, '--disable', 'enable_request_compression', '-c', 'model_provider="proof"',
    '-c', 'model_providers.proof.name="proof"', '-c', `model_providers.proof.base_url="${base}/v1"`,
    '-c', 'model_providers.proof.wire_api="responses"', '-c', 'model_providers.proof.env_key="CODEX_API_KEY"',
    '-c', 'model_providers.proof.supports_websockets=false', '-m', 'gpt-5.5')
  const env = { PATH: `${path.dirname(process.execPath)}:${process.env.PATH}`, HOME: home, CODEX_HOME: home,
    TMPDIR: work, CODEX_API_KEY: 'synthetic-key', RUNTIME, ...podEnv, AGENTHUB_SKILL_REMINDER: '0' }
  const child = spawn(CODEX, args, { cwd: work, env, detached: true, stdio: ['ignore', 'pipe', 'pipe'] })
  let output = ''
  child.stdout.on('data', d => { output += d }); child.stderr.on('data', d => { output += d })
  const timer = setTimeout(() => { try { process.kill(-child.pid, 'SIGKILL') } catch {} }, 120000)
  await new Promise(resolve => child.on('close', resolve))
  clearTimeout(timer)
  server.close()

  // Keyed by the tools this script called, never by a name read off the request.
  const outputs = Object.fromEntries(CALLS.map(call => {
    const item = requests.flatMap(r => r.input || []).find(i => /_call_output$/.test(i.type) && i.call_id === `call-${call.tool}`)
    return [call.tool, item === undefined ? undefined : typeof item.output === 'string' ? item.output : JSON.stringify(item.output)]
  }))
  const hookRuns = fs.existsSync(hookLog) ? fs.readFileSync(hookLog, 'utf8').trim().split('\n').filter(Boolean).map(l => JSON.parse(l)) : []
  const offered = (requests.find(r => r.tools?.length)?.tools || []).flatMap(t => t.tools ? t.tools.map(i => `${t.name}/${i.name}`) : [t.name]).filter(n => /agenthub_sessions/.test(n))
  fs.writeFileSync(path.join(work, 'result.json'), JSON.stringify({ label, body, outputs, hookRuns, offered, output: output.slice(-4000) }, null, 2))
  await api('DELETE', `/sessions/${id}`)
  return { id, outputs, hookRuns, offered }
}

const rows = []
for (const [label, body] of Object.entries(CASES)) {
  const r = await runCase(label, body)
  console.log(`\n## ${label}  (session ${r.id})`)
  console.log(`offered: ${r.offered.join(', ') || '(none)'}`)
  for (const call of CALLS) {
    const out = r.outputs[call.tool]
    const hook = r.hookRuns.filter(h => (h.tool || '').includes(call.tool))
    const verdict = out === undefined ? 'not called' : /Blocked by the session policy/.test(out) ? 'BLOCKED' : 'ran'
    const events = hook.map(h => `${h.event}:${h.tool}->${h.output ? JSON.parse(h.output).hookSpecificOutput?.permissionDecision || JSON.parse(h.output).hookSpecificOutput?.decision?.behavior : 'allow(no output)'}`)
    console.log(`  ${call.tool}: ${verdict}; hook events: ${JSON.stringify(events)}`)
    console.log(`    result: ${JSON.stringify((out || '').slice(0, 160))}`)
    rows.push({ label, tool: call.tool, verdict, hook: hook.length })
  }
}
fs.writeFileSync(path.join(OUT, 'summary.json'), JSON.stringify(rows, null, 2))
console.log(`\nevidence: ${OUT}`)
