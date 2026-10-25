// #253 proof: duplicate secret names are stored; a session, project, grant, rename or request
// answer that would make one session receive two secrets landing in the same place is refused.
// Runs against the REAL backend (dev auth) on 127.0.0.1:8080 acting as the chart's service account
// on a real kube-apiserver, real Postgres, the REAL stdio MCP server (mcp/agenthub, personal token)
// and the REAL in-pod agenthub_sessions MCP server (callback token). Rig: README.md in
// tests/SessionResourcesProof; no S3 needed here.
// Run: node proof.mjs <admin-kubeconfig> <repo> <backend-log>
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import os from 'node:os'
import path from 'node:path'

const [adminKubeconfig, repo, backendLog] = process.argv.slice(2)
const here = createRequire(import.meta.url)
const pg = here('pg')
const sdk = createRequire(path.join(repo, 'mcp/agenthub/package.json'))
const { Client } = sdk('@modelcontextprotocol/sdk/client/index.js')
const { StdioClientTransport } = sdk('@modelcontextprotocol/sdk/client/stdio.js')

const NS = 'agenthub-sessions'
const API = 'http://127.0.0.1:8080/api'
const kubectl = (...args) => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', adminKubeconfig, '-n', NS, ...args], { encoding: 'utf8' })
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
/** The keys and decoded values of a session's copy (sessionenv-/sessionsecrets-<id>). */
function copy(kind, id) {
  try {
    const data = JSON.parse(kubectl('get', 'secret', `${kind}-${id}`, '-o', 'json')).data || {}
    return Object.fromEntries(Object.entries(data).map(([k, v]) => [k, Buffer.from(v, 'base64').toString()]))
  } catch { return null }
}
async function podExists(id) {
  for (let i = 0; i < 40; i++) {
    try { kubectl('get', 'pod', `session-${id}`, '-o', 'name'); return true } catch { await sleep(250) }
  }
  return false
}
const VALUES = { prod: 'prod-value-0001', staging: 'staging-value-0002', kube1: 'kubeconfig-one-0003', kube2: 'kubeconfig-two-0004', x: 'x-value-00000005' }
const base = { mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' }
const sessionCount = async () => (await api('GET', '/sessions')).json.length

// A. Two secrets under one name are two secrets.
const put = async (name, delivery, value, description) => (await api('POST', '/secrets', { name, delivery, value, description })).json
const prod = await put('API_KEY', 'env', VALUES.prod, 'production')
const staging = await put('API_KEY', 'env', VALUES.staging, 'staging')
const kube1 = await put('kubeconfig', 'file', VALUES.kube1, 'cluster one')
const kube2 = await put('kubeconfig', 'file', VALUES.kube2, 'cluster two')
const x = await put('API_KEY_STAGING', 'env', VALUES.x, 'renamed later')
let listed = (await api('GET', '/secrets')).json
check(prod.id !== staging.id && listed.filter(s => s.name === 'API_KEY').length === 2,
  'a second API_KEY is stored as a second secret', `ids ${prod.id}, ${staging.id}`)
check(!JSON.stringify(listed).includes('value-000'), 'the listing carries no value')

// B. A project selecting both is refused; selecting one is fine.
let r = await api('POST', '/projects', { name: 'Deploys', secretIds: [prod.id, staging.id] })
check(r.status === 400 && r.text.includes('would select 2 secrets as the environment variable API_KEY'),
  'a new project selecting both API_KEYs is a 400', r.text)
check(!(await api('GET', '/projects')).json.some(p => p.name === 'Deploys'), 'and was not created')
const project = (await api('POST', '/projects', { name: 'Deploys', secretIds: [prod.id, kube1.id] })).json
check(!!project?.id, 'a project selecting one API_KEY and one kubeconfig is created')

// C. A session following the project gets exactly the project's API_KEY; a clashing grant is refused.
const s1 = (await api('POST', '/sessions', { ...base, title: 'deploy-prod', projectId: project.id })).json
check(await podExists(s1.id), 'the session in the project was spawned (pod admitted)')
check(copy('sessionenv', s1.id)?.API_KEY === VALUES.prod && copy('sessionsecrets', s1.id)?.kubeconfig === VALUES.kube1,
  'its copies hold the production API_KEY and cluster one kubeconfig')
r = await api('PATCH', `/sessions/${s1.id}`, { secretGrants: [{ secretId: staging.id }] })
check(r.status === 400 && r.text.includes('This session would receive 2 secrets as the environment variable API_KEY'),
  'granting the staging API_KEY to it is a 400 naming the variable', r.text)
r = await api('PATCH', `/sessions/${s1.id}`, { secretGrants: [{ secretId: kube2.id }] })
check(r.status === 400 && r.text.includes('as the file ~/.secrets/kubeconfig'), 'granting the second kubeconfig is a 400 naming the file', r.text)
check((await api('GET', `/sessions/${s1.id}`)).json.secretGrants?.length === 0 && copy('sessionenv', s1.id)?.API_KEY === VALUES.prod,
  'nothing was stored and the copy still holds the production value')
r = await api('PATCH', `/projects/${project.id}`, { secretIds: [prod.id, kube1.id, staging.id] })
check(r.status === 400 && r.text.includes('“Deploys” would select 2 secrets'), 'widening the project to both is a 400', r.text)
check(JSON.stringify((await api('GET', '/projects')).json.find(p => p.id === project.id).secretIds) === JSON.stringify([prod.id, kube1.id]),
  'the project selection is unchanged')

// D. Outside a project: an explicit list with both is refused before a row exists.
const before = await sessionCount()
r = await api('POST', '/sessions', { ...base, title: 'both', secretIds: [prod.id, staging.id] })
check(r.status === 400 && r.text.includes('This session would receive 2 secrets as the environment variable API_KEY'), 'creating a session with both is a 400', r.text)
r = await api('POST', '/sessions', { ...base, title: 'grant-clash', secretIds: [kube1.id], secretGrants: [{ secretId: kube2.id }] })
check(r.status === 400 && r.text.includes('~/.secrets/kubeconfig'), 'a selection plus a clashing grant is a 400 too', r.text)
check(await sessionCount() === before, 'no refused session was stored')
const s2 = (await api('POST', '/sessions', { ...base, title: 'staging-bot', secretIds: [staging.id, x.id], secretGrants: [{ secretId: kube2.id }] })).json
check(await podExists(s2.id) && copy('sessionenv', s2.id)?.API_KEY === VALUES.staging,
  'a session with the staging API_KEY alone gets the staging value')

// E. Renaming a secret into a clash is refused before it is stored; another rename goes through.
r = await api('POST', '/secrets', { id: x.id, name: 'API_KEY', delivery: 'env' })
check(r.status === 400 && r.text.includes(`“staging-bot” (${s2.id}) would receive 2 secrets as the environment variable API_KEY`),
  'renaming API_KEY_STAGING to API_KEY is a 400 naming the session', r.text)
check((await api('GET', '/secrets')).json.find(s => s.id === x.id).name === 'API_KEY_STAGING', 'the store still has the old name')
r = await api('POST', '/secrets', { id: x.id, name: 'TOKEN2', delivery: 'env' })
const env2 = copy('sessionenv', s2.id)
check(r.status === 200 && r.json.id === x.id && env2.TOKEN2 === VALUES.x && !('API_KEY_STAGING' in env2),
  'a rename that clashes with nothing keeps the id and rewrites the copy under the new name', Object.keys(env2).join(','))

// F. A personal API token: the HTTP API answers with the sentence and a code, and the stdio MCP
// server, which passes on backend codes only (mcp/agenthub/errors.mjs), with that code.
const token = (await api('POST', '/tokens', { name: 'proof-253' })).json.token
const remote = await fetch(API + '/remote/sessions', { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
  body: JSON.stringify({ ...base, title: 'token-both', secretIds: [prod.id, staging.id] }) })
const remoteBody = await remote.json().catch(() => ({}))
check(remote.status === 400 && remoteBody.code === 'secret_conflict' && remoteBody.error?.includes('as the environment variable API_KEY'),
  'POST /api/remote/sessions with both is a 400 with code and sentence', JSON.stringify(remoteBody))
const stdio = new Client({ name: 'proof', version: '1' })
await stdio.connect(new StdioClientTransport({
  command: process.execPath, args: [path.join(repo, 'mcp/agenthub/server.mjs')],
  env: { ...process.env, AGENTHUB_URL: 'http://127.0.0.1:8080', AGENTHUB_TOKEN: token }, stderr: 'ignore'
}))
let tool = await stdio.callTool({ name: 'session_create', arguments: { title: 'mcp-both', agent: 'Claude', authMode: 'ApiKey', secretIds: `${prod.id},${staging.id}` } })
let text = tool.content.map(c => c.text).join('')
check(tool.isError && text === '{"error":"secret_conflict"}', 'MCP session_create with both answers secret_conflict', text)
tool = await stdio.callTool({ name: 'session_create', arguments: { title: 'mcp-one', agent: 'Claude', authMode: 'ApiKey', secretIds: prod.id } })
check(!tool.isError, 'MCP session_create with one of them succeeds')
await stdio.close()

// G. The in-pod MCP server: the owner answering a request with a clashing secret is refused,
// the request stays open, and a refusal then reaches the agent as "not provided".
const db = new pg.Client({ connectionString: process.env.PG || 'postgres://postgres@127.0.0.1:54329/agenthub253' })
await db.connect()
const callback = (await db.query('select callback_token from sessions where id = $1', [s2.id])).rows[0].callback_token
await db.end()
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'mcp253-'))
fs.cpSync(path.join(repo, 'agent-runtime/sessions'), path.join(work, 'sessions'), { recursive: true })
fs.symlinkSync(path.join(repo, 'agent-runtime/session-agent/node_modules'), path.join(work, 'node_modules'))
const inPod = new Client({ name: 'proof', version: '1' })
await inPod.connect(new StdioClientTransport({
  command: process.execPath, args: [path.join(work, 'sessions/server.mjs')], stderr: 'ignore',
  env: { ...process.env, AGENTHUB_CALLBACK_URL: `http://127.0.0.1:8080/internal/sessions/${s2.id}`, AGENTHUB_CALLBACK_TOKEN: callback,
    AGENTHUB_SESSION_ID: s2.id, AGENTHUB_SECRETS_DIR: path.join(work, 'secrets') }
}))
const asked = inPod.callTool({ name: 'secret_request', arguments: { name: 'kubeconfig-new', delivery: 'file', purpose: 'cluster access', waitMinutes: 1 } },
  undefined, { timeout: 120_000 })
let request
for (let i = 0; i < 40 && !request; i++) {
  request = (await api('GET', '/secret-requests')).json?.find(q => q.sessionId === s2.id && q.status === 'pending')
  if (!request) await sleep(250)
}
r = await api('POST', `/secret-requests/${request.id}/provide`, { secretId: kube1.id })
check(r.status === 400 && r.text.includes('as the file ~/.secrets/kubeconfig'), 'answering with a kubeconfig the session already has one of is a 400', r.text)
check((await api('GET', `/secret-requests/${request.id}`)).json.status === 'pending', 'the request is still open')
await api('POST', `/secret-requests/${request.id}/refuse`)
tool = await asked
text = tool.content.map(c => c.text).join('')
check(text.includes('not provided') && !text.includes('value-000'), 'the agent gets "not provided" and no value', text.slice(0, 160))
await inPod.close()

// H. No value in the backend log.
const log = fs.readFileSync(backendLog, 'utf8')
check(!Object.values(VALUES).some(v => log.includes(v)), 'no secret value in the backend log')

console.log(`\n${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
