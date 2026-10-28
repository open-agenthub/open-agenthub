// #324 proof against the REAL backend: startup refusal, the API key on REST / WebSocket / admin
// endpoints, the internal callback, personal API token and remote MCP schemes untouched, and the
// insecure development mode. README.md lists the rig. Run:
//   node proof.mjs <branch AgentHub.Api.dll> [baseline AgentHub.Api.dll]
// The baseline (v1.4.0) run shows the defect itself: no configuration, and every admin endpoint
// answers an anonymous caller.
import { execFileSync, spawnSync } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import http from 'node:http'
import { HTTP, HTTPS, readKey, startBackend, waitHealthy } from './backend.mjs'

const [dll, baselineDll] = process.argv.slice(2)
const RIG = process.env.RIG || process.env.HOME + '/rig'
// Trust the rig's own certificate on 8443 instead of switching verification off. Node reads
// NODE_EXTRA_CA_CERTS only at startup, so the script runs itself once more with it set.
if (!process.env.NODE_EXTRA_CA_CERTS) {
  const r = spawnSync(process.execPath, process.argv.slice(1), {
    stdio: 'inherit', env: { ...process.env, NODE_EXTRA_CA_CERTS: RIG + '/tls/cert.pem' }
  })
  process.exit(r.status ?? 1)
}
const KEY = readKey()
const WRONG = crypto.randomBytes(36).toString('base64url')
const kubectl = (...a) => execFileSync(process.env.KUBECTL || 'kubectl', ['-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
function check(ok, what, detail = '') {
  results.push(ok)
  // Never print a response body that could carry the key: detail is built by the caller.
  console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`)
}
const bearer = k => (k ? { Authorization: `Bearer ${k}` } : {})
// Loopback is a configured trusted proxy in this rig. This proof presents wrong keys on
// purpose all over, so each request is its own
// client unless a section pins one, as the rate-limit section does.
let clientCounter = 0
const nextClient = () => ({ 'X-Forwarded-For': `198.18.${Math.floor(++clientCounter / 250)}.${clientCounter % 250}` })
async function call(method, path, key, body, extraHeaders = {}) {
  const r = await fetch(HTTP + path, {
    method, redirect: 'manual',
    headers: { ...nextClient(), ...bearer(key), ...(body ? { 'Content-Type': 'application/json' } : {}), ...extraHeaders },
    body: body && JSON.stringify(body)
  })
  const text = await r.text()
  let json = null
  try { json = JSON.parse(text) } catch { }
  return { status: r.status, json, text, headers: r.headers }
}
// A raw upgrade request, so the HTTP status of a refused WebSocket is visible (a browser only
// reports "closed").
function wsStatus(path) {
  return new Promise((resolve, reject) => {
    const req = http.request(HTTP + path, {
      headers: { ...nextClient(), Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64') }
    })
    req.on('upgrade', (res, socket) => { socket.destroy(); resolve(res.statusCode) })
    req.on('response', res => { res.resume(); resolve(res.statusCode) })
    req.on('error', reject)
    req.setTimeout(15000, () => { req.destroy(); resolve('timeout') })
    req.end()
  })
}
async function runUntilExit(mode, log) {
  const child = startBackend(dll, mode, log)
  const code = await new Promise(resolve => {
    const t = setTimeout(() => { child.kill('SIGKILL'); resolve('still running') }, 60000)
    child.on('exit', c => { clearTimeout(t); resolve(c) })
  })
  let answered = false
  try { await fetch(HTTP + '/healthz'); answered = true } catch { }
  return { code, log: fs.readFileSync(log, 'utf8'), answered }
}
async function stop(child) {
  if (child.exitCode !== null) return
  child.kill('SIGTERM')
  await new Promise(r => { child.on('exit', r); setTimeout(r, 15000) })
}

// ---------------------------------------------------------------------------------------------
console.log('\n== 0. Baseline v1.4.0: no auth configured ==')
if (baselineDll) {
  const child = startBackend(baselineDll, 'none', RIG + '/proof-baseline.log')
  check(await waitHealthy(child), 'baseline starts with no auth configured at all')
  for (const [method, path] of [['GET', '/api/sessions'], ['GET', '/api/admin/overview'], ['GET', '/api/admin/roles']]) {
    const r = await call(method, path, null)
    check(r.status === 200, `baseline answers an anonymous ${method} ${path} (the defect)`, `HTTP ${r.status}`)
  }
  // The rig has no object storage, so the cleanup itself fails; what matters is that it ran.
  const cleanup = await call('POST', '/api/admin/session-storage-cleanup/dry-run', null, {})
  check(cleanup.status !== 401 && cleanup.status !== 403, 'baseline runs an anonymous admin cleanup dry-run (the defect)', `HTTP ${cleanup.status}`)
  await stop(child)
} else console.log('(skipped: no baseline dll given)')

// ---------------------------------------------------------------------------------------------
console.log('\n== 1. Startup is refused without an auth configuration ==')
for (const [mode, expect] of [['none', 'No authentication is configured'], ['short', 'shorter than 32 characters'], ['lowEntropy', 'fewer than 8 different characters'], ['insecureTypo', 'No authentication is configured']]) {
  const r = await runUntilExit(mode, `${RIG}/proof-refuse-${mode}.log`)
  check(r.code === 1, `mode "${mode}": the process exits with code 1`, `exit ${r.code}`)
  check(r.log.includes('FATAL: ') && r.log.includes(expect), `mode "${mode}": one FATAL line naming the fix`,
    r.log.split('\n').find(l => l.startsWith('FATAL')) || '(none)')
  check(!r.answered, `mode "${mode}": nothing ever listened on 8080`)
}

// ---------------------------------------------------------------------------------------------
console.log('\n== 2. API key mode ==')
const logPath = RIG + '/proof-apikey.log'
let backend = startBackend(dll, 'apiKey', logPath)
check(await waitHealthy(backend), 'backend starts with an API key')

const cfg = await call('GET', '/api/config')
check(cfg.json?.authMode === 'apiKey' && !cfg.text.includes(KEY), '/api/config: authMode apiKey, anonymous, without the key')

const endpoints = [
  ['GET', '/api/sessions'], ['GET', '/api/projects'], ['GET', '/api/admin/access'], ['GET', '/api/admin/overview'],
  ['GET', '/api/admin/roles'], ['POST', '/api/admin/session-storage-cleanup/dry-run'], ['GET', '/api/tokens']
]
for (const [method, path] of endpoints) {
  const body = method === 'POST' ? {} : undefined
  const none = await call(method, path, null, body)
  const wrong = await call(method, path, WRONG, body)
  const right = await call(method, path, KEY, body)
  check(none.status === 401 && wrong.status === 401, `${method} ${path}: 401 without a key and with a wrong one`, `${none.status}/${wrong.status}`)
  // The cleanup dry-run fails with 500 in the rig (no object storage) after authorisation.
  const passed = path.endsWith('/dry-run') ? right.status !== 401 && right.status !== 403 : right.status >= 200 && right.status < 300
  check(passed, `${method} ${path}: the right key gets through`, `HTTP ${right.status}`)
}
check((await call('GET', '/api/sessions', null)).headers.get('www-authenticate')?.startsWith('Bearer'), '401 carries a Bearer challenge')
check((await call('GET', `/api/sessions?access_token=${KEY}`, null)).status === 401, 'the key in a query string is ignored outside /ws')
check((await call('GET', '/api/sessions', KEY.slice(0, -1))).status === 401, 'a key one character short is refused')

const access = await call('GET', '/api/admin/access', KEY)
check(access.json?.isAdmin === true && access.json?.user === 'admin', 'the key acts as the configured owner, an admin', JSON.stringify(access.json))

// A real session, so the WebSocket routes and the internal callback have something to act on.
check((await call('PUT', '/api/credentials', KEY, { anthropicApiKey: 'sk-test-proof' })).status === 204, 'credentials stored as the key owner')
const created = await call('POST', '/api/sessions', KEY, { mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey', title: 'proof-324' })
const session = created.json
check(created.status === 200 && !!session?.id, 'session created with the key', `HTTP ${created.status}`)
let pod = null
for (let i = 0; i < 60 && !pod; i++) { try { pod = JSON.parse(kubectl('get', 'pod', `session-${session.id}`, '-o', 'json')) } catch { await sleep(500) } }
const agentEnv = name => pod?.spec.containers.find(c => c.name === 'agent').env.find(e => e.name === name)?.value
const callbackToken = agentEnv('AGENTHUB_CALLBACK_TOKEN')
check(!!callbackToken, 'the session pod exists on the real API server with a callback token')
check(!JSON.stringify(pod).includes(KEY), 'the API key does not reach the session pod spec')

for (const kind of ['events', 'terminal', 'shell']) {
  const p = `/ws/sessions/${encodeURIComponent(session.id)}/${kind}`
  const none = await wsStatus(p)
  const wrong = await wsStatus(`${p}?access_token=${WRONG}`)
  check(none === 401 && wrong === 401, `WebSocket ${kind}: 401 without a key and with a wrong one`, `${none}/${wrong}`)
}
check(await wsStatus(`/ws/sessions/${encodeURIComponent(session.id)}/events?access_token=${encodeURIComponent(KEY)}`) === 101,
  'WebSocket events: the right key in access_token upgrades (101)')
const wsHeader = await new Promise(resolve => {
  const req = http.request(HTTP + `/ws/sessions/${encodeURIComponent(session.id)}/events`, {
    headers: { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64'), Authorization: `Bearer ${KEY}` }
  })
  req.on('upgrade', (res, s) => { s.destroy(); resolve(res.statusCode) })
  req.on('response', res => { res.resume(); resolve(res.statusCode) })
  req.end()
})
check(wsHeader === 101, 'WebSocket events: the key in the Authorization header upgrades too (101)')
const term = await wsStatus(`/ws/sessions/${encodeURIComponent(session.id)}/terminal?access_token=${encodeURIComponent(KEY)}`)
check(term !== 401 && term !== 403, 'WebSocket terminal: the right key passes authentication (the pod has no agent in the rig)', `HTTP ${term}`)

// --- Schemes that must be untouched ---
const internalPath = `/internal/sessions/${encodeURIComponent(session.id)}/turn-started`
const internalOk = await call('POST', internalPath, null, {}, { 'X-Agent-Token': callbackToken })
const internalBad = await call('POST', internalPath, null, {}, { 'X-Agent-Token': 'wrong' })
check(internalOk.status >= 200 && internalOk.status < 300, 'internal callback: the session token works without the API key', `HTTP ${internalOk.status}`)
check(internalBad.status === 401 || internalBad.status === 403 || internalBad.status === 404, 'internal callback: a wrong session token is still refused', `HTTP ${internalBad.status}`)
const internalWithKey = await call('POST', internalPath, KEY, {}, {})
check(internalWithKey.status === 401 || internalWithKey.status === 403 || internalWithKey.status === 404, 'internal callback: the API key is no substitute for the session token', `HTTP ${internalWithKey.status}`)

const scrollback = await call('GET', `/internal/sessions/${encodeURIComponent(session.id)}/scrollback`, null, undefined, { 'X-Agent-Token': callbackToken })
check(scrollback.status !== 401 && scrollback.status !== 403, 'internal GET with the session token is not caught by the API key scheme', `HTTP ${scrollback.status}`)

const token = await call('POST', '/api/tokens', KEY, { name: 'proof-324' })
const personal = token.json?.token
check(token.status === 200 && personal?.startsWith('oah_'), 'a personal API token is issued to the key owner')
const remote = await call('GET', '/api/remote/sessions', personal)
check(remote.status === 200 && JSON.stringify(remote.json).includes(session.id), 'remote API: the personal token lists the owner\'s session, no API key involved', `HTTP ${remote.status}`)
check((await call('GET', '/api/remote/sessions', null)).status === 401, 'remote API: still 401 without a token')
check((await call('GET', '/api/sessions', personal)).status === 401, 'a personal token is not accepted as the API key')

// --- Remote MCP: OAuth 2.1 with PKCE; the consent screen now asks for the key ---
const redirect = 'http://127.0.0.1:9/callback'
const reg = await (await fetch(HTTPS + '/connect/register', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ redirect_uris: [redirect], client_name: 'proof-324' }) })).json()
check(!!reg.client_id, 'MCP: anonymous client registration still works')
const verifier = crypto.randomBytes(32).toString('base64url')
const challenge = crypto.createHash('sha256').update(verifier).digest('base64url')
const authorizeQuery = new URLSearchParams({ response_type: 'code', client_id: reg.client_id, redirect_uri: redirect, code_challenge: challenge, code_challenge_method: 'S256', scope: 'mcp', resource: HTTPS + '/mcp', state: 'proof' })
const silent = await fetch(`${HTTPS}/connect/authorize?${authorizeQuery}&prompt=none`, { redirect: 'manual' })
check(silent.status === 302 && /error=login_required/.test(silent.headers.get('location') || ''), 'MCP: prompt=none cannot get a code without the key', silent.headers.get('location')?.replace(/state=[^&]*/, '') || `HTTP ${silent.status}`)

// &amp; last, so an escaped "&amp;quot;" stays the literal text "&quot;".
const unescapeHtml = v => v.replace(/&#43;|&#x2B;/g, '+').replace(/&quot;/g, '"').replace(/&amp;/g, '&')
async function consent() {
  const r = await fetch(`${HTTPS}/connect/authorize?${authorizeQuery}`, { redirect: 'manual' })
  const html = await r.text()
  const cookies = r.headers.getSetCookie().map(c => c.split(';')[0]).join('; ')
  const fields = {}
  for (const m of html.matchAll(/<input type="hidden" name="([^"]+)" value="([^"]*)">/g))
    fields[unescapeHtml(m[1])] = unescapeHtml(m[2])
  return { status: r.status, html, cookies, fields }
}
async function post(page, extra) {
  return fetch(`${HTTPS}/connect/authorize`, {
    method: 'POST', redirect: 'manual',
    headers: { ...nextClient(), 'Content-Type': 'application/x-www-form-urlencoded', Cookie: page.cookies, ...(page.client || {}) },
    body: new URLSearchParams({ ...page.fields, 'submit.Accept': 'accept', ...extra })
  })
}
let page = await consent()
check(page.status === 200 && page.html.includes('name="password"') && page.html.includes('type="password"'), 'MCP: the consent screen asks for the API key')
const wrongPost = await post(page, { password: WRONG })
const wrongHtml = await wrongPost.text()
check(wrongPost.status === 401 && wrongHtml.includes('not valid') && !wrongHtml.includes(WRONG), 'MCP: a wrong key re-renders the form with 401, without echoing it', `HTTP ${wrongPost.status}`)
const emptyPost = await post(page, {})
check(emptyPost.status === 401, 'MCP: approving without a key is refused', `HTTP ${emptyPost.status}`)
page = await consent()
const okPost = await post(page, { password: KEY })
const location = okPost.headers.get('location') || ''
const code = new URL(location, HTTPS).searchParams.get('code')
check(okPost.status === 302 && location.startsWith(redirect) && !!code, 'MCP: the right key yields an authorization code', `HTTP ${okPost.status}`)
// Approval was stored, but must not let the next authorize skip the key.
const again = await consent()
check(again.status === 200 && again.html.includes('name="password"'), 'MCP: a stored approval does not skip the key next time')
const tok = await (await fetch(HTTPS + '/connect/token', {
  method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: redirect, client_id: reg.client_id, code_verifier: verifier, resource: HTTPS + '/mcp' })
})).json()
check(!!tok.access_token, 'MCP: code exchanged for an access token')
async function mcp(bearerToken, body, sessionId) {
  const r = await fetch(HTTPS + '/mcp', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', ...bearer(bearerToken), ...(sessionId ? { 'Mcp-Session-Id': sessionId } : {}) },
    body: JSON.stringify(body)
  })
  const text = await r.text()
  const data = text.split('\n').filter(l => l.startsWith('data:')).map(l => JSON.parse(l.slice(5)))[0] ?? (() => { try { return JSON.parse(text) } catch { return null } })()
  return { status: r.status, data, sid: r.headers.get('mcp-session-id') }
}
const init = await mcp(tok.access_token, { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'proof', version: '1' } } })
check(init.status === 200 && !!init.data?.result, 'MCP: /mcp initialises with the OAuth token', `HTTP ${init.status}`)
await mcp(tok.access_token, { jsonrpc: '2.0', method: 'notifications/initialized' }, init.sid)
const tools = await mcp(tok.access_token, { jsonrpc: '2.0', id: 2, method: 'tools/list' }, init.sid)
const toolNames = (tools.data?.result?.tools || []).map(t => t.name)
check(toolNames.length > 0, 'MCP: tools/list answers', `${toolNames.length} tools`)
const listTool = toolNames.find(n => /session.*list|list.*session/i.test(n))
if (listTool) {
  const listed = await mcp(tok.access_token, { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: listTool, arguments: {} } }, init.sid)
  check(JSON.stringify(listed.data).includes(session.id), `MCP: ${listTool} shows the key owner's session`)
}
check((await mcp(KEY, { jsonrpc: '2.0', id: 4, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'p', version: '1' } } })).status === 401, 'MCP: the API key itself is not an MCP token (scheme untouched)')
check((await mcp(null, { jsonrpc: '2.0', id: 5, method: 'tools/list' })).status === 401, 'MCP: /mcp without a token is 401')

// --- Throttling wrong keys ---
const attacker = { 'X-Forwarded-For': '203.0.113.50' }
const codes = []
for (let i = 0; i < 10; i++) codes.push((await call('GET', '/api/sessions', crypto.randomBytes(36).toString('base64url'), undefined, attacker)).status)
check(codes.every(c => c === 401), '10 wrong keys from one client: each 401', codes.join(','))
const throttled = await call('GET', '/api/sessions', KEY, undefined, attacker)
check(throttled.status === 429 && throttled.headers.get('retry-after') === '60', 'the 11th attempt gets 429 with Retry-After, even with the right key', `HTTP ${throttled.status}, Retry-After ${throttled.headers.get('retry-after')}`)
const wsThrottled = await new Promise(resolve => {
  const req = http.request(HTTP + `/ws/sessions/${encodeURIComponent(session.id)}/events?access_token=${encodeURIComponent(KEY)}`, {
    headers: { ...attacker, Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64') }
  })
  req.on('upgrade', (res, sock) => { sock.destroy(); resolve(res.statusCode) })
  req.on('response', res => { res.resume(); resolve(res.statusCode) })
  req.end()
})
check(wsThrottled === 429, 'the WebSocket route is throttled for that client too', `HTTP ${wsThrottled}`)
page = await consent()
page.client = attacker
const consentThrottled = await post(page, { password: KEY })
check(consentThrottled.status === 429, 'and so is the MCP consent form', `HTTP ${consentThrottled.status}`)
check((await call('GET', '/api/sessions', KEY)).status === 200, 'another client (the owner behind the same ingress) is unaffected')
// Behind the ingress every client shares one socket address; a flood through it must not
// lock out the owner (no per-address bucket on purpose).
const flood = []
for (let i = 0; i < 300; i++) flood.push(call('GET', '/api/sessions', 'wrong-' + i, undefined, { 'X-Forwarded-For': `192.0.2.${i % 250}, 10.77.${Math.floor(i / 250)}.${i % 250}` }))
const floodCodes = (await Promise.all(flood)).map(r => r.status)
check(floodCodes.every(c => c === 401), '300 wrong keys from rotating clients through one address: all 401', [...new Set(floodCodes)].join(','))
check((await call('GET', '/api/sessions', KEY, undefined, { 'X-Forwarded-For': '198.51.100.20' })).status === 200, 'and the owner with the right key still gets in')
for (let i = 0; i < 30; i++) await call('GET', '/api/remote/sessions', personal, undefined, { 'X-Forwarded-For': '203.0.113.77' })
check((await call('GET', '/api/sessions', KEY, undefined, { 'X-Forwarded-For': '203.0.113.77' })).status === 200,
  '30 personal-token calls from one client do not count as wrong keys')

await stop(backend)
const log = fs.readFileSync(logPath, 'utf8')
check(/warn: AgentHub\.Api\.Auth\.ApiKeyAuthHandler\[0\]\s+Rejected a wrong API key from 203\.0\.113\.50 for GET \/api\/sessions/.test(log),
  'each wrong key is logged with the client, without the value')
check(!log.includes(KEY), 'the backend log never contains the key', `${log.split('\n').length} log lines`)
check(!log.includes(WRONG), 'nor a wrong key that was presented')
check(log.includes('Authentication: API key, acting as owner admin'), 'the log names the mode and the owner')

// ---------------------------------------------------------------------------------------------
console.log('\n== 3. Insecure development mode ==')
backend = startBackend(dll, 'insecure', RIG + '/proof-insecure.log')
check(await waitHealthy(backend), 'backend starts with Auth:InsecureDevMode=true')
const insecureCfg = await call('GET', '/api/config')
check(insecureCfg.json?.authMode === 'insecureDev', '/api/config reports insecureDev, so the UI warns')
check((await call('GET', '/api/sessions', null)).status === 200, 'anonymous requests work, as before')
const devAccess = await call('GET', '/api/admin/access', null)
check(devAccess.json?.user === 'dev' && devAccess.json?.isAdmin === true, 'and are admin user "dev"', JSON.stringify(devAccess.json))
check(await wsStatus(`/ws/sessions/${encodeURIComponent(session.id)}/events`) !== 401, 'WebSockets need no token either')
await stop(backend)
const insecureLog = fs.readFileSync(RIG + '/proof-insecure.log', 'utf8')
check(/warn: AgentHub\.Api\[0\]\s+INSECURE DEVELOPMENT MODE/.test(insecureLog), 'a warning is logged on start', insecureLog.split('\n').find(l => l.includes('INSECURE'))?.trim().slice(0, 100))

const failed = results.filter(r => !r).length
console.log(`\n${results.length - failed}/${results.length} checks passed`)
process.exit(failed ? 1 : 0)
