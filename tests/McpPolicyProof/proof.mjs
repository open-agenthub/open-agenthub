// #514 end to end against the real backend: an owner blocks MCP tools on a running Claude session,
// and the real Claude Code in that session's pod calls a blocked tool, an allowed tool and a tool
// of a blocked server. Runs an interactive session and an interactive one with auto-approve, then
// pauses and resumes the first to read what the rebuilt pod is told at start.
//
// Run: LICENSE_KEY=<ES256 private key PEM> REPO=<repo> KUBECTL=<kubectl> KUBECONFIG_ADMIN=<rig kubeconfig> \
//        CLAUDE_BIN_DIR=<dir with the pinned claude> node proof.mjs <scratch dir>
// The backend must be one built with the matching public key (see README) on 127.0.0.1:8080.
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync, spawn } from 'node:child_process'

const [scratch] = process.argv.slice(2)
const REPO = process.env.REPO
const BASE = 'http://127.0.0.1:8080'
const here = path.dirname(new URL(import.meta.url).pathname)
const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
const check = (ok, what, detail = '') => { results.push({ ok, what, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }
async function call(method, p, body) {
  const r = await fetch(BASE + p, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) })
  const text = await r.text()
  return { status: r.status, body: text ? (() => { try { return JSON.parse(text) } catch { return text } })() : null }
}
const kubectl = (...a) => execFileSync(process.env.KUBECTL || 'kubectl',
  ['--kubeconfig', process.env.KUBECONFIG_ADMIN, '-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
fs.mkdirSync(scratch, { recursive: true })

// ------------------------------------------------------------ Enterprise licence (local test key)
const b64url = b => Buffer.from(b).toString('base64url')
const head = b64url(JSON.stringify({ alg: 'ES256', typ: 'JWT' }))
const claims = b64url(JSON.stringify({ lid: crypto.randomUUID(), org: 'MCP policy proof', email: 'proof@example.com', seats: 10,
  plan: 'subscription', validUntil: new Date(Date.now() + 7 * 864e5).toISOString() }))
const signature = crypto.sign('sha256', Buffer.from(`${head}.${claims}`),
  { key: fs.readFileSync(process.env.LICENSE_KEY, 'utf8'), dsaEncoding: 'ieee-p1363' })
const activated = await call('POST', '/api/admin/license', { token: `${head}.${claims}.${b64url(signature)}` })
check(activated.status === 200 && activated.body?.valid !== false, 'the Enterprise test licence is active', `HTTP ${activated.status}`)

// Names as an owner types them into the dialog: the configured server names, and tool names in
// the mcp__<server>__<tool> form the dialog asks for.
const POLICY = { blockedServers: ['team-docs', 'ops.tools'], blockedTools: ['mcp__docs__search', 'mcp__docs__get-data.v2'] }
const docsServer = (name, logFile) => ({ command: process.execPath, args: [path.join(here, 'docs-mcp.mjs'), name], env: { DOCS_MCP_LOG: logFile } })

async function scenario(label, autoApprove) {
  const dir = path.join(scratch, label)
  fs.rmSync(dir, { recursive: true, force: true })
  const mcpLog = path.join(scratch, `${label}-mcp.log`)
  fs.rmSync(mcpLog, { force: true })
  const created = await call('POST', '/api/sessions', {
    title: `mcp policy ${label}`, mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey', autoApprove,
    prompt: 'MCP-PROOF: call the docs tools',
    mcpConfigJson: JSON.stringify({ mcpServers: { docs: docsServer('docs', mcpLog), 'team-docs': docsServer('team-docs', mcpLog), 'ops.tools': docsServer('ops.tools', mcpLog) } })
  })
  const id = created.body?.id
  check(created.status === 200 && !!id, `${label}: session created`, `HTTP ${created.status} ${id || JSON.stringify(created.body).slice(0, 200)}`)
  // Set after the pod exists, as an owner restricting a session that is already running does.
  const policy = await call('PUT', `/api/ee/sessions/${id}/mcp-policy`, POLICY)
  check(policy.status === 200, `${label}: the owner blocked ${JSON.stringify(POLICY)}`, `HTTP ${policy.status}`)

  const pod = spawn(process.execPath, [path.join(here, 'pod.mjs'), id, dir], { stdio: 'inherit', env: process.env })
  const modelLog = () => { try { return fs.readFileSync(path.join(dir, 'model.log'), 'utf8') } catch { return '' } }
  const asked = []
  const end = Date.now() + 240_000
  while (Date.now() < end && !/ final$/m.test(modelLog())) {
    await sleep(1000)
    const pending = (await call('GET', `/api/sessions/${id}/permissions`)).body || []
    for (const request of pending) {
      if (asked.some(a => a.id === request.id)) continue
      asked.push(request)
      console.log(`${label}: approval requested for ${request.tool}; the owner allows it`)
      await call('POST', `/api/sessions/${id}/permissions/${request.id}`, { decision: 'allow' })
    }
  }
  await sleep(1000)
  pod.kill('SIGTERM')
  await sleep(3500)

  const model = modelLog()
  const calls = (() => { try { return fs.readFileSync(mcpLog, 'utf8') } catch { return '' } })()
  const result = tool => (model.match(new RegExp(`tool_result ${tool.replace(/[$()*+.?[\\\]^{|}]/g, '\\$&')}: (.*)`)) || [])[1] || '(none)'
  console.log(`--- ${label} model.log\n${model}--- ${label} MCP server log\n${calls}---`)
  check(/ final$/m.test(model), `${label}: the turn finished`)
  check(/Blocked by the session MCP sharing policy/.test(result('mcp__docs__search')) && !/docs tools\/call search/.test(calls),
    `${label}: mcp__docs__search (blocked tool) denied, never reached the server`, result('mcp__docs__search'))
  check(/Blocked by the session MCP sharing policy/.test(result('mcp__team-docs__search')) && !/team-docs tools\/call/.test(calls),
    `${label}: mcp__team-docs__search (blocked server) denied, never reached the server`, result('mcp__team-docs__search'))
  check(/EXECUTED docs\/read/.test(result('mcp__docs__read')) && /docs tools\/call read/.test(calls),
    `${label}: mcp__docs__read (allowed) ran`, result('mcp__docs__read'))
  // The CLI spells "." as "_" in the names it reports (ops.tools → mcp__ops_tools__…); the owner's
  // typed names have to block those calls too.
  const plan = Object.fromEntries([...model.matchAll(/plan (\S+) -> (\S+)/g)].map(m => [m[1], m[2]]))
  for (const [entry, what, server, tool] of [
    ['^mcp__docs__get.data.v2$', 'blocked tool get-data.v2', 'docs', 'get-data.v2'],
    ['^mcp__ops.tools__search$', 'tool of blocked server ops.tools', 'ops.tools', 'search']]) {
    const name = plan[entry] || '(not offered)'
    const ran = calls.split('\n').some(l => l.includes(` ${server} tools/call ${tool}`))
    check(/Blocked by the session MCP sharing policy/.test(result(name)) && !ran,
      `${label}: ${what}, called as ${name}, denied and never reached the server`, result(name))
  }
  // ToolSearch is an ordinary built-in tool and asks like one in an interactive session.
  const tools = asked.map(a => a.tool).filter(t => t.startsWith('mcp__'))
  const blockedAsked = tools.filter(t => t !== 'mcp__docs__read')
  check(blockedAsked.length === 0 && (autoApprove ? tools.length === 0 : tools.length === 1),
    `${label}: no approval for a blocked tool; ${autoApprove ? 'none at all (auto-approve)' : 'the allowed tool asked the owner'}`, JSON.stringify(tools))
  return { id, model, calls, asked: tools }
}

const interactive = await scenario('interactive', false)
const auto = await scenario('auto-approve', true)

// A pod built while the restriction exists is told so at start: that seed is what the hook falls
// back to when the backend cannot be reached.
const env = id => (JSON.parse(kubectl('get', 'pod', `session-${id}`, '-o', 'json')).spec.containers[0].env || [])
  .find(e => e.name === 'AGENTHUB_MCP_POLICY')?.value
const before = (() => { try { return env(interactive.id) } catch { return '(no pod)' } })()
const paused = await call('POST', `/api/sessions/${interactive.id}/pause`)
for (let i = 0; i < 30; i++) { try { kubectl('get', 'pod', `session-${interactive.id}`); await sleep(1000) } catch { break } }
const resumed = await call('POST', `/api/sessions/${interactive.id}/resume`)
let after = '(no pod)'
for (let i = 0; i < 30 && after === '(no pod)'; i++) { try { after = env(interactive.id) ?? '(unset)' } catch { await sleep(1000) } }
check(after === '1', 'a pod rebuilt after the restriction was set starts with AGENTHUB_MCP_POLICY=1',
  `first pod: ${before}; pause HTTP ${paused.status}, resume HTTP ${resumed.status}; rebuilt pod: ${after}`)

fs.writeFileSync(path.join(scratch, 'results.json'), JSON.stringify({ results, interactive, auto }, null, 2))
const failed = results.filter(r => !r.ok).length
console.log(`\n${results.length - failed}/${results.length} checks passed`)
process.exit(failed ? 1 : 0)
