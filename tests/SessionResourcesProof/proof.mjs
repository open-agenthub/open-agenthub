// #215 proof: per-session CPU/memory limits and restarting a running session to apply them,
// against the REAL backend (dev auth) on 127.0.0.1:8080, a real kube-apiserver (the backend acting
// as the chart's service account), real Postgres and real S3-compatible storage. README.md says
// how to bring those up. Run: node proof.mjs <admin-kubeconfig> <repo>
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import os from 'node:os'
import path from 'node:path'

const [adminKubeconfig, repo] = process.argv.slice(2)
const here = createRequire(import.meta.url)
const pg = here('pg')
const runtime = createRequire(path.join(repo, 'agent-runtime/session-agent/package.json'))
const { Client } = runtime('@modelcontextprotocol/sdk/client/index.js')
const { StdioClientTransport } = runtime('@modelcontextprotocol/sdk/client/stdio.js')

const NS = 'agenthub-sessions'
const API = 'http://127.0.0.1:8080/api'
const kubectl = (...args) => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', adminKubeconfig, '-n', NS, ...args], { encoding: 'utf8' })
const apply = yaml => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', adminKubeconfig, '-n', NS, 'apply', '-f', '-'], { input: yaml, encoding: 'utf8' })
async function api(method, p, body) {
  const r = await fetch(API + p, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) })
  const text = await r.text()
  let json = null
  try { json = JSON.parse(text) } catch { }
  return { status: r.status, json, text }
}
const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
function check(ok, what, detail = '') {
  results.push(ok)
  console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`)
}
function pod(id) {
  try { return JSON.parse(kubectl('get', 'pod', `session-${id}`, '-o', 'json')) } catch { return null }
}
function resources(p) {
  const r = p.spec.containers.find(c => c.name === 'agent').resources
  return `${r.requests.cpu}/${r.limits.cpu} cpu, ${r.requests.memory}/${r.limits.memory} memory`
}
const envOf = (p, name) => p.spec.containers.find(c => c.name === 'agent').env.find(e => e.name === name)?.value
async function running(id, notUid) {
  for (let i = 0; i < 60; i++) {
    const p = pod(id)
    if (p && p.metadata.uid !== notUid && p.status?.phase === 'Running' && !p.metadata.deletionTimestamp) return p
    await sleep(500)
  }
  throw new Error(`session-${id} did not come up`)
}
const base = { mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' }
const count = async () => (await api('GET', '/sessions')).json.length

// 1. Nothing changes unless configured: a session without limits gets agent.maxCpu/maxMemory.
const plain = (await api('POST', '/sessions', { ...base, title: 'plain' })).json
let p = await running(plain.id)
check(resources(p) === '500m/2 cpu, 1Gi/4Gi memory', 'without limits the pod keeps the instance default', resources(p))

// 2. Own limits up to the ceiling (8 / 16Gi here); the API server admits the pod (PSA baseline).
const big = await api('POST', '/sessions', { ...base, title: 'big', cpu: '3', cpuLimit: '6', memory: '2Gi', memoryLimit: '12Gi' })
p = await running(big.json.id)
check(big.status === 200 && resources(p) === '3/6 cpu, 2Gi/12Gi memory', 'a session with its own limits is admitted with them', resources(p))
check(JSON.stringify(big.json.resources) === '{"cpuRequest":"3","cpuLimit":"6","memoryRequest":"2Gi","memoryLimit":"12Gi"}',
  'SessionInfo reports the effective request and limit')

// 3. Refused before anything is stored.
const before = await count()
for (const [body, expected] of [
  [{ cpuLimit: '9' }, 'above the ceiling of 8 on this instance'],
  [{ cpu: '3' }, 'above the maximum of 2 on this instance'],
  [{ cpu: '3', cpuLimit: '2500m' }, "above this session's cpu limit of 2500m"],
  [{ memoryLimit: '8m' }, 'The suffix m means milli'],
]) {
  const r = await api('POST', '/sessions', { ...base, title: 'refused', ...body })
  check(r.status === 400 && r.text.includes(expected), `create ${JSON.stringify(body)} is a 400`, r.text.slice(0, 120))
}
check(await count() === before, 'no refused session was stored')

// 4. Editing a running session, then restarting it to apply: pause → resume, conversation kept.
const id = plain.id
const oldPod = pod(id)
const edit = await api('PATCH', `/sessions/${id}`, { memory: '2Gi', memoryLimit: '8Gi' })
check(edit.json.resourcesPendingRestart === true && pod(id).metadata.uid === oldPod.metadata.uid,
  'the edit is stored, the running pod is untouched and reported as pending a restart')
const applied = await api('POST', `/sessions/${id}/apply-resources`)
p = await running(id, oldPod.metadata.uid)
check(applied.status === 200 && resources(p) === '500m/2 cpu, 2Gi/8Gi memory', 'apply-resources replaced the pod with the new values', resources(p))
check(envOf(p, 'AGENTHUB_RESUME') === '1' && envOf(p, 'AGENTHUB_CLAUDE_SESSION_ID') === envOf(oldPod, 'AGENTHUB_CLAUDE_SESSION_ID'),
  'the new pod resumes the same conversation', envOf(p, 'AGENTHUB_CLAUDE_SESSION_ID'))
const again = await api('POST', `/sessions/${id}/apply-resources`)
check(again.status === 200 && pod(id).metadata.uid === p.metadata.uid, 'a second apply restarts nothing')

// 5. A LimitRange below the new limit: refused by the API server's dry run while the session runs.
apply(`apiVersion: v1
kind: LimitRange
metadata: { name: proof-max }
spec: { limits: [ { type: Container, max: { memory: 10Gi } } ] }`)
await api('PATCH', `/sessions/${id}`, { memoryLimit: '12Gi' })
const refused = await api('POST', `/sessions/${id}/apply-resources`)
const still = pod(id)
check(refused.status === 409 && refused.text.includes('keeps running unchanged') && refused.text.includes('maximum memory usage per Container is 10Gi'),
  'a LimitRange refusal is a 409 with the API server\'s reason', refused.text.slice(0, 200))
check(still.metadata.uid === p.metadata.uid && still.status.phase === 'Running' && !still.metadata.deletionTimestamp,
  'the session keeps running on its old pod')
check((await api('GET', `/sessions/${id}`)).json.memoryLimit === '12Gi', 'the edit itself is kept for the next start')
kubectl('delete', 'limitrange', 'proof-max')

// 6. A ResourceQuota: the running pod still counts during the dry run, so the message says so; a
// manual pause and resume then fits.
await api('DELETE', `/sessions/${big.json.id}`)
for (let i = 0; i < 40 && pod(big.json.id); i++) await sleep(250)
apply(`apiVersion: v1
kind: ResourceQuota
metadata: { name: proof-quota }
spec: { hard: { limits.memory: 14Gi } }`)
for (let i = 0; i < 40; i++) {
  const q = JSON.parse(kubectl('get', 'resourcequota', 'proof-quota', '-o', 'json'))
  if (q.status?.used?.['limits.memory']) break
  await sleep(500)
}
await api('PATCH', `/sessions/${id}`, { memoryLimit: '10Gi' })
const quota = await api('POST', `/sessions/${id}/apply-resources`)
check(quota.status === 409 && quota.text.includes('exceeded quota') && quota.text.includes('still counts against the namespace quota'),
  'a quota refusal names the quota and the running pod counting against it', quota.text.slice(0, 220))
check(pod(id).metadata.uid === p.metadata.uid, 'the session keeps running')
await api('POST', `/sessions/${id}/pause`)
for (let i = 0; i < 40 && pod(id); i++) await sleep(250)
await api('POST', `/sessions/${id}/resume`)
const resumed = await running(id, p.metadata.uid)
check(resources(resumed) === '500m/2 cpu, 2Gi/10Gi memory', 'pause and resume then fit the quota', resources(resumed))
kubectl('delete', 'resourcequota', 'proof-quota')

// 7. Persistence: the limits are columns of their own, read back by the real store.
const db = new pg.Client({ host: '127.0.0.1', port: 54329, user: 'postgres', database: process.env.PGDATABASE || 'agenthub215' })
await db.connect()
const row = (await db.query('SELECT cpu, memory, cpu_limit, memory_limit, callback_token FROM sessions WHERE id=$1', [id])).rows[0]
check(row.cpu_limit === null && row.memory_limit === '10Gi' && row.memory === '2Gi', 'Postgres holds the limits (null = default)', JSON.stringify({ ...row, callback_token: undefined }))

// 8. The in-pod agenthub_sessions MCP server: session_create with limits for a child. Laid out as
// in the runtime images, /opt/session-agent/{sessions,node_modules}, so its imports resolve.
const layout = fs.mkdtempSync(path.join(os.tmpdir(), 'session-agent-'))
fs.cpSync(path.join(repo, 'agent-runtime/sessions'), path.join(layout, 'sessions'), { recursive: true })
fs.symlinkSync(path.join(repo, 'agent-runtime/session-agent/node_modules'), path.join(layout, 'node_modules'))
const inPod = new Client({ name: 'proof', version: '1.0.0' })
await inPod.connect(new StdioClientTransport({
  command: process.execPath, args: [path.join(layout, 'sessions/server.mjs')],
  env: { ...process.env, AGENTHUB_CALLBACK_URL: `http://127.0.0.1:8080/internal/sessions/${id}`, AGENTHUB_CALLBACK_TOKEN: row.callback_token, AGENTHUB_SESSION_ID: id }
}))
const createTool = (await inPod.listTools()).tools.find(t => t.name === 'session_create')
check(!!createTool.inputSchema.properties.cpuLimit && !!createTool.inputSchema.properties.memoryLimit, 'in-pod session_create offers cpuLimit and memoryLimit')
const child = JSON.parse((await inPod.callTool({ name: 'session_create', arguments: { title: 'child', cpu: '1', cpuLimit: '4', memoryLimit: '6Gi' } })).content[0].text)
p = await running(child.id)
check(resources(p) === '1/4 cpu, 1Gi/6Gi memory' && child.resources?.cpuLimit === '4', 'the child pod has the limits, and the reply reports them', resources(p))
const tooBig = await inPod.callTool({ name: 'session_create', arguments: { title: 'child', cpuLimit: '64' } })
check(tooBig.isError === true && JSON.stringify(tooBig.content).includes('400'), 'an in-pod create above the ceiling fails', JSON.stringify(tooBig.content).slice(0, 160))
await inPod.close()

// 9. The token API through the stdio MCP server (personal API token).
const token = (await api('POST', '/tokens', { name: 'proof' })).json.token
const stdio = new Client({ name: 'proof', version: '1.0.0' })
await stdio.connect(new StdioClientTransport({
  command: process.execPath, args: [path.join(repo, 'mcp/agenthub/server.mjs')],
  env: { ...process.env, AGENTHUB_URL: 'http://127.0.0.1:8080', AGENTHUB_TOKEN: token }
}))
const remote = JSON.parse((await stdio.callTool({ name: 'session_create', arguments: { title: 'remote', cpu: '2', cpuLimit: '3', memory: '3Gi', memoryLimit: '5Gi' } })).content[0].text)
p = await running(remote.id)
check(resources(p) === '2/3 cpu, 3Gi/5Gi memory', 'a token-API create through the stdio MCP server gets the limits', resources(p))
const remoteRefused = await stdio.callTool({ name: 'session_create', arguments: { title: 'remote', memory: '6Gi', memoryLimit: '5Gi' } })
// The stdio server answers with a fixed error vocabulary by design (errors.mjs), so the reason
// itself is checked on the token API directly.
check(remoteRefused.isError === true && JSON.stringify(remoteRefused.content).includes('agenthub_http_400'),
  'a stdio create with a request above its limit fails with a 400', JSON.stringify(remoteRefused.content))
const direct = await fetch('http://127.0.0.1:8080/api/remote/sessions', { method: 'POST',
  headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
  body: JSON.stringify({ ...base, title: 'remote', memory: '6Gi', memoryLimit: '5Gi' }) })
const directText = await direct.text()
check(direct.status === 400 && directText.includes("above this session's memory limit of 5Gi"), 'the token API names the reason', directText)
await stdio.close()
await db.end()

for (const s of (await api('GET', '/sessions')).json) await api('DELETE', `/sessions/${s.id}`)
console.log(`${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
