// #330 reproduction over HTTP: the real in-pod agenthub_sessions MCP server (stdio, real SDK
// client) with a real session's callback token against the real backend; the owner answers with
// the two calls Settings → Secrets makes on save (store the value, then provide). Covers a session
// the owner created and one an agent spawned through session_create.
// Run: node repro.mjs <repo> [postgres-url]
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createRequire } from 'node:module'

const [repo, pgUrl = 'postgres://postgres@127.0.0.1:54329/agenthub330'] = process.argv.slice(2)
const here = createRequire(import.meta.url)
const pg = here('pg')
const sdk = createRequire(path.join(repo, 'agent-runtime/session-agent/package.json'))
const { Client } = sdk('@modelcontextprotocol/sdk/client/index.js')
const { StdioClientTransport } = sdk('@modelcontextprotocol/sdk/client/stdio.js')
const BASE = 'http://127.0.0.1:8080'
// API_KEY_FILE: the backend runs in API-key mode and owner calls carry the key.
const ownerKey = process.env.API_KEY_FILE ? fs.readFileSync(process.env.API_KEY_FILE, 'utf8').trim() : null
async function call(method, p, body, headers = ownerKey ? { Authorization: `Bearer ${ownerKey}` } : {}) {
  const r = await fetch(BASE + p, { method, headers: { 'Content-Type': 'application/json', ...headers }, body: body && JSON.stringify(body) })
  const text = await r.text()
  return { status: r.status, body: text ? (() => { try { return JSON.parse(text) } catch { return text } })() : null }
}
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }

// The image's layout: the server next to a node_modules of the session agent.
const mcpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mcp-'))
fs.cpSync(path.join(repo, 'agent-runtime/sessions'), path.join(mcpDir, 'sessions'), { recursive: true })
fs.symlinkSync(path.join(repo, 'agent-runtime/session-agent/node_modules'), path.join(mcpDir, 'node_modules'))

const db = new pg.Client({ connectionString: pgUrl })
await db.connect()
const tokenOf = async id => (await db.query('select callback_token from sessions where id = $1', [id])).rows[0].callback_token

async function mcpFor(session) {
  const transport = new StdioClientTransport({
    command: process.execPath, args: [path.join(mcpDir, 'sessions/server.mjs')],
    env: { ...process.env, AGENTHUB_CALLBACK_URL: `${BASE}/internal/sessions/${session.id}`,
      AGENTHUB_CALLBACK_TOKEN: await tokenOf(session.id), AGENTHUB_SESSION_ID: session.id,
      AGENTHUB_SECRETS_DIR: fs.mkdtempSync(path.join(os.tmpdir(), 'secrets-')) }
  })
  const client = new Client({ name: 'repro', version: '1.0.0' })
  await client.connect(transport)
  return client
}
const json = r => JSON.parse(r.content[0].text)

const run = Date.now().toString(36)
const parent = (await call('POST', '/api/sessions', { title: `owner-created ${run}`, mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' })).body
const parentMcp = await mcpFor(parent)
const child = json(await parentMcp.callTool({ name: 'session_create', arguments: { title: `agent-spawned ${run}`, agent: 'Claude', authMode: 'ApiKey' } }))
console.log('sessions', parent.id, child.id ?? JSON.stringify(child))
const childMcp = await mcpFor(child)

for (const [label, session, mcp] of [['owner-created', parent, parentMcp], ['agent-spawned', child, childMcp]]) {
  const name = `KEY_${label.replace('-', '_').toUpperCase()}_${run}`
  const asked = mcp.callTool({ name: 'secret_request', arguments: { name, delivery: 'env', purpose: `repro ${label}`, waitMinutes: 1 } },
    undefined, { timeout: 120_000 })
  let open = []
  for (let i = 0; i < 40 && !open.length; i++) { await new Promise(r => setTimeout(r, 250)); open = (await call('GET', `/api/secret-requests?sessionId=${session.id}`)).body }
  check(open.length === 1, `${label}: the request is open for the owner`)
  // What SecretsPane.save does: store the value, then answer the request with the stored id.
  const saved = await call('POST', '/api/secrets', { name, delivery: 'env', value: `value-${run}-${label}` })
  const provided = await call('POST', `/api/secret-requests/${open[0].id}/provide`, { secretId: saved.body.id })
  check(saved.status === 200, `${label}: the value is stored`, String(saved.status))
  check(provided.status === 200, `${label}: the answer grants it`, `${provided.status} ${JSON.stringify(provided.body)}`)
  const answer = json(await asked)
  check(answer.status === 'provided', `${label}: the agent's call returns provided`, JSON.stringify(answer))
  const still = (await call('GET', `/api/secret-requests?sessionId=${session.id}`)).body
  check(still.length === 0, `${label}: the request notice is gone`, `${still.length} open`)
  const listed = json(await mcp.callTool({ name: 'secrets_list', arguments: {} }))
  check(listed.secrets.some(s => s.name === name), `${label}: secrets_list shows it`, JSON.stringify(listed.secrets.map(s => s.name)))
  await call('DELETE', `/api/secrets/${saved.body.id}`)
}
// ---- Only the owner's answer widens a ceiling, and a refused answer leaves nothing stored.
{
  const openFor = async name => {
    for (let i = 0; i < 40; i++) {
      const found = ((await call('GET', `/api/secret-requests?sessionId=${child.id}`)).body || []).find(q => q.name === name)
      if (found) return found
      await new Promise(r => setTimeout(r, 250))
    }
  }
  const ask = name => childMcp.callTool({ name: 'secret_request', arguments: { name, delivery: 'env', waitMinutes: 1 } }, undefined, { timeout: 120_000 }).catch(() => null)
  const held = (await call('POST', '/api/secrets', { name: `HELD_${run}`, delivery: 'env', value: `held-${run}` })).body
  const other = (await call('POST', '/api/secrets', { name: `OTHER_${run}`, delivery: 'file', value: `other-${run}` })).body
  // The agent-spawned session gets HELD the only way it can: the owner answering its request.
  const first = ask(`HELD_${run}_ALIAS`)
  const r1 = await openFor(`HELD_${run}_ALIAS`)
  check((await call('POST', `/api/secret-requests/${r1.id}/provide`, { secretId: held.id })).status === 200, 'the owner answers with a stored secret')
  await first
  const second = ask(`CLASH_${run}`)
  const r2 = await openFor(`CLASH_${run}`)
  if (ownerKey) {
    for (const [who, s] of [['the requesting session', child], ['its parent', parent]]) {
      const token = await tokenOf(s.id)
      const bearer = await call('POST', `/api/secret-requests/${r2.id}/provide`, { secretId: other.id }, { Authorization: `Bearer ${token}` })
      const agent = await call('POST', `/api/secret-requests/${r2.id}/provide`, { secretId: other.id }, { 'X-Agent-Token': token })
      check(bearer.status === 401 && agent.status === 401, `${who}'s callback token cannot answer on the owner API`, `${bearer.status}/${agent.status}`)
    }
  }
  // Answered with a NEW secret under the name of one the session holds: the hub refuses the
  // clash, and the new secret must not stay stored.
  const before = (await call('GET', '/api/secrets')).body.length
  const clash = await call('POST', `/api/secret-requests/${r2.id}/provide`, { secret: { name: `HELD_${run}`, delivery: 'env', value: `clash-${run}` } })
  const after = (await call('GET', '/api/secrets')).body
  check(clash.status === 400, 'a refused answer is reported', `${clash.status} ${JSON.stringify(clash.body).slice(0, 160)}`)
  check(after.length === before && after.filter(s => s.name === `HELD_${run}`).length === 1, 'a refused answer leaves no new secret stored', `${before} -> ${after.length}`)
  check(((await call('GET', `/api/secret-requests?sessionId=${child.id}`)).body || []).some(q => q.id === r2.id), 'a refused answer leaves the request open')
  await call('POST', `/api/secret-requests/${r2.id}/refuse`)
  await second
  // The agent cannot widen its own child's ceiling: a secret it holds only by the owner's
  // non-inheritable answer, and one it never held, are both refused at the spawn.
  console.log('agent-spawned ceiling now', (await db.query('select secret_ceiling from sessions where id = $1', [child.id])).rows[0].secret_ceiling)
  for (const [what, id] of [['a secret it holds by the owner\'s answer', held.id], ['a secret outside its ceiling', other.id]]) {
    const r = await childMcp.callTool({ name: 'session_create', arguments: { title: 'grandchild', agent: 'Claude', authMode: 'ApiKey', secretIds: id } })
    const text = r.content?.[0]?.text || ''
    check(r.isError && /403|secret_not_allowed/.test(text), `the agent cannot spawn a child with ${what}`, text.slice(0, 120))
  }
  for (const s of [held, other]) await call('DELETE', `/api/secrets/${s.id}`)
}
await parentMcp.close(); await childMcp.close(); await db.end()
console.log(`${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
