// #501 proof: the REAL backend runs in development mode, then API-key mode, then OIDC (local
// issuer, real PKCE and JWT validation) on one Postgres, a real kube-apiserver and real object
// storage. Data created in the first two modes is handed to two OIDC users, once with and once
// without stored credentials, in real Chromium and over HTTP. See README.md.
//   node proof.mjs <AgentHub.Api.dll> <chrome-headless-shell> <outdir>
import { execFileSync, spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import os from 'node:os'
import path from 'node:path'

const require = createRequire(path.join(process.cwd(), 'package.json'))
const pg = require('pg')
const { S3Client, ListObjectsV2Command } = require('@aws-sdk/client-s3')
const { chromium } = require('playwright-core')

const [dll, chrome, outdir] = process.argv.slice(2)
if (!dll || !chrome || !outdir) { console.error('usage: node proof.mjs <dll> <chrome> <outdir>'); process.exit(2) }
fs.mkdirSync(outdir, { recursive: true })
const HERE = path.dirname(new URL(import.meta.url).pathname)
const REPO = path.resolve(HERE, '../..')
const RIG = process.env.RIG || os.homedir() + '/rig'
const KUBECTL = process.env.KUBECTL || 'kubectl'
const KC = RIG + '/k8s/kubeconfig'
const NS = 'agenthub-sessions'
const PG_BASE = process.env.PG || 'Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres'
const DBNAME = `handover501_${Date.now()}`
const UI = 'http://localhost:5173'
const ISSUER = `${UI}/__identity`
const API = 'http://127.0.0.1:8080'
const s3key = fs.readFileSync(RIG + '/s3/key.txt', 'utf8')
const S3 = { id: s3key.match(/Key ID:\s*(\S+)/)[1], secret: s3key.match(/Secret key:\s*(\S+)/)[1], bucket: process.env.S3_BUCKET || 'agenthub-proof' }
// Generated per run and never printed.
const API_KEY = crypto.randomBytes(24).toString('hex')
const SECRET = { dev: 'sk-ant-api03-dev-' + crypto.randomBytes(8).toString('hex'),
  admin: 'sk-ant-api03-admin-' + crypto.randomBytes(8).toString('hex'),
  alice: 'sk-ant-api03-alice-' + crypto.randomBytes(8).toString('hex') }

const results = []
let failed = 0
function check(name, ok, detail = '') {
  results.push(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`)
  console.log(results.at(-1))
  if (!ok) failed++
}
const sleep = ms => new Promise(r => setTimeout(r, ms))
const ownerKey = o => 'u-' + crypto.createHash('sha256').update(o).digest('hex').slice(0, 16)
const k = (...a) => execFileSync(KUBECTL, ['--kubeconfig', KC, '-n', NS, ...a], { encoding: 'utf8' })
const kjson = (...a) => { try { return JSON.parse(k(...a, '-o', 'json')) } catch { return null } }
const secretValue = (name, key) => { const s = kjson('get', 'secret', name); return s?.data?.[key] ? Buffer.from(s.data[key], 'base64').toString() : null }

// ---- services --------------------------------------------------------------------------------
const children = []
const spawnLogged = (name, cmd, args, opts = {}) => {
  const out = fs.openSync(path.join(outdir, name + '.log'), 'w')
  const child = spawn(cmd, args, { stdio: ['ignore', out, out], ...opts })
  children.push(child)
  return child
}
process.on('exit', () => { for (const c of children) try { c.kill('SIGTERM') } catch { } })

const pgConn = (db) => {
  const m = Object.fromEntries(PG_BASE.split(';').filter(Boolean).map(p => p.split('=')).map(([a, b]) => [a.toLowerCase(), b]))
  return new pg.Client({ host: m.host, port: +m.port, user: m.username, database: db ?? m.database })
}
{ const c = pgConn(); await c.connect(); await c.query(`CREATE DATABASE ${DBNAME}`); await c.end() }
const db = pgConn(DBNAME)

k('get', 'ns') // reachable
try { execFileSync(KUBECTL, ['--kubeconfig', KC, 'create', 'ns', NS], { stdio: 'ignore' }) } catch { }
try { k('create', 'sa', 'agenthub-agent') } catch { }

const home = fs.mkdtempSync(path.join(os.tmpdir(), 'proof501-'))
fs.mkdirSync(path.join(home, '.kube'))
fs.copyFileSync(KC, path.join(home, '.kube', 'config'))
const dpCert = path.join(home, 'dp-cert.pem'), dpKey = path.join(home, 'dp-key.pem')
execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=proof501',
  '-keyout', dpKey, '-out', dpCert], { stdio: 'ignore' })

spawnLogged('identity', process.execPath, [path.join(HERE, 'identity.mjs')], { env: { ...process.env, PROOF_ISSUER: ISSUER, UI } })
spawnLogged('vite', process.execPath, [path.join(REPO, 'frontend/node_modules/vite/bin/vite.js'), '--config', path.join(HERE, 'vite.config.mjs'), '--port', '5173', '--strictPort'],
  { cwd: path.join(REPO, 'frontend') })
spawnLogged('kubelet', process.execPath, [path.join(REPO, 'tests/SessionResourcesProof/kubelet.mjs')], { env: { ...process.env, RIG, KUBECTL } })

let backend = null
async function startBackend(mode) {
  if (backend) { backend.kill('SIGTERM'); await new Promise(r => backend.on('exit', r)); backend = null }
  const env = { ...process.env, HOME: home, ASPNETCORE_URLS: API, ASPNETCORE_ENVIRONMENT: 'Production',
    ConnectionStrings__Postgres: `${PG_BASE.replace(/Database=[^;]*/, 'Database=' + DBNAME)};Pooling=true`,
    AgentHub__Namespace: NS, AgentHub__CallbackBaseUrl: API, AgentHub__AgentPort: '7699',
    AgentHub__ClaudeAgentImage: 'registry.example.com/agent-claude:proof', AgentHub__AgentImage: 'registry.example.com/agent:proof',
    AgentHub__PauseGracePeriodSeconds: '5', Browser__Enabled: 'false', Ee__Admins: 'root',
    S3__ServiceUrl: 'http://127.0.0.1:3910', S3__Region: 'local', S3__Bucket: S3.bucket, S3__AccessKey: S3.id, S3__SecretKey: S3.secret,
    DataProtection__CertificatePem: fs.readFileSync(dpCert, 'utf8'), DataProtection__PrivateKeyPem: fs.readFileSync(dpKey, 'utf8') }
  for (const v of ['Auth__ApiKey', 'Auth__ApiKeyOwner', 'Auth__InsecureDevMode', 'Oidc__Authority']) delete env[v]
  if (mode === 'dev') env.Auth__InsecureDevMode = 'true'
  if (mode === 'apiKey') Object.assign(env, { Auth__ApiKey: API_KEY })
  if (mode === 'oidc') Object.assign(env, { Oidc__Authority: ISSUER, Oidc__RequireHttpsMetadata: 'false', Oidc__ClientId: 'proof501' })
  backend = spawnLogged('backend-' + mode, process.env.DOTNET || 'dotnet', [dll], { env })
  for (let i = 0; i < 240; i++) {
    if (backend.exitCode !== null || backend.signalCode) throw new Error(`backend (${mode}) exited`)
    try { if ((await fetch(`${API}/healthz`)).ok) return } catch { }
    await sleep(500)
  }
  throw new Error('backend not healthy')
}

async function api(auth, method, p, body, headers = {}) {
  const res = await fetch(API + p, { method, headers: { ...(auth ? { Authorization: `Bearer ${auth}` } : {}),
    ...(body !== undefined ? { 'content-type': 'application/json' } : {}), ...headers },
    body: body === undefined ? undefined : typeof body === 'string' ? body : JSON.stringify(body) })
  const text = await res.text()
  let json = null; try { json = JSON.parse(text) } catch { }
  return { status: res.status, json, text }
}
async function must(auth, method, p, body) {
  const r = await api(auth, method, p, body)
  if (r.status >= 300) throw new Error(`${method} ${p} -> ${r.status} ${r.text.slice(0, 300)}`)
  return r.json
}

// The authorization code flow with PKCE against the fixture, as a non-browser client would run it.
async function signIn(user) {
  const verifier = crypto.randomBytes(32).toString('base64url')
  const challenge = crypto.createHash('sha256').update(verifier).digest('base64url')
  const u = new URL(ISSUER + '/authorize')
  const redirect = `${UI}/auth/callback`
  Object.entries({ user, response_type: 'code', client_id: 'proof501', redirect_uri: redirect, code_challenge: challenge,
    code_challenge_method: 'S256', state: 's', nonce: 'n', scope: 'openid' }).forEach(([a, b]) => u.searchParams.set(a, b))
  const r = await fetch(u, { redirect: 'manual' })
  const code = new URL(r.headers.get('location')).searchParams.get('code')
  const t = await fetch(ISSUER + '/token', { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: redirect, code_verifier: verifier, client_id: 'proof501' }) })
  return (await t.json()).access_token
}

async function waitFor(fn, what, tries = 120) {
  for (let i = 0; i < tries; i++) { const v = await fn(); if (v) return v; await sleep(500) }
  throw new Error('timed out: ' + what)
}
const podRunning = id => waitFor(() => kjson('get', 'pod', `session-${id}`)?.status?.phase === 'Running', `pod ${id} running`)

async function s3list(prefix) {
  const client = new S3Client({ endpoint: 'http://127.0.0.1:3910', region: 'local', forcePathStyle: true,
    credentials: { accessKeyId: S3.id, secretAccessKey: S3.secret } })
  const r = await client.send(new ListObjectsV2Command({ Bucket: S3.bucket, Prefix: prefix }))
  return (r.Contents || []).map(o => o.Key)
}

// ---- 1. development mode: the data of "dev" -------------------------------------------------
await startBackend('dev')
await must(null, 'PUT', '/api/credentials', { anthropicApiKey: SECRET.dev, gitUserName: 'Dev Proof' })
const devPat = await must(null, 'POST', '/api/credentials/git-pats', { kind: 'GitHub', host: 'github.com', token: 'ghp_' + crypto.randomBytes(18).toString('hex'), label: 'dev token' })
const devSecret = await must(null, 'POST', '/api/secrets', { name: 'DEV_TOKEN', delivery: 'env', value: 'dev-' + crypto.randomBytes(6).toString('hex') })
const devProject = await must(null, 'POST', '/api/projects', { name: 'Pilot' })
const devWork = await must(null, 'POST', '/api/sessions', { title: 'dev work', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey', projectId: devProject.id })
const devLive = await must(null, 'POST', '/api/sessions', { title: 'dev live', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' })
await podRunning(devWork.id); await podRunning(devLive.id)
await must(null, 'POST', `/api/sessions/${devWork.id}/pause`)

// ---- 2. API-key mode: the data of "admin" (the quickstart default) ---------------------------
await startBackend('apiKey')
const KEY = API_KEY
check('API-key mode acts as admin', (await must(KEY, 'GET', '/api/admin/access')).user === 'admin')
await must(KEY, 'PUT', '/api/credentials', { anthropicApiKey: SECRET.admin, gitUserName: 'Admin Proof', gitUserEmail: 'admin@example.com' })
const adminPat = await must(KEY, 'POST', '/api/credentials/git-pats', { kind: 'GitHub', host: 'github.com', token: 'ghp_' + crypto.randomBytes(18).toString('hex'), label: 'admin token' })
const adminSecret = await must(KEY, 'POST', '/api/secrets', { name: 'ADMIN_TOKEN', delivery: 'env', value: 'admin-' + crypto.randomBytes(6).toString('hex') })
const tools = await must(KEY, 'POST', '/api/projects', { name: 'Tools' })
const adminWork = await must(KEY, 'POST', '/api/sessions', { title: 'admin work', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey', projectId: tools.id })
const adminLogin = await must(KEY, 'POST', '/api/sessions', { title: 'admin login', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription' })
const nightly = await must(KEY, 'POST', '/api/sessions', { title: 'nightly', mode: 'Scheduled', schedule: '0 3 * * *', prompt: 'report', agent: 'Claude', authMode: 'ApiKey' })
await podRunning(adminWork.id); await podRunning(adminLogin.id)
// The pod's own upload of a Claude login, through the real internal route and its callback token.
await db.connect()
const cb = (await db.query('SELECT callback_token FROM sessions WHERE id = $1', [adminLogin.id])).rows[0].callback_token
const login = JSON.stringify({ claudeAiOauth: { accessToken: 'sk-ant-oat01-' + crypto.randomBytes(12).toString('hex'),
  refreshToken: 'sk-ant-ort01-' + crypto.randomBytes(12).toString('hex'), expiresAt: Date.now() + 3600e3, scopes: ['user:inference'] } })
const up = await api(null, 'PUT', `/internal/sessions/${adminLogin.id}/claude-credentials`, login, { 'X-Agent-Token': cb, 'content-type': 'application/json' })
check('admin has a stored Claude login', up.status < 300 && !!kjson('get', 'secret', `claude-${ownerKey('admin')}`), `status ${up.status}`)
await must(KEY, 'POST', `/api/sessions/${adminWork.id}/pause`)
await must(KEY, 'POST', `/api/sessions/${adminLogin.id}/pause`)
check('scheduled session has a CronJob labelled for admin',
  kjson('get', 'cronjob', `session-${nightly.id}`)?.metadata?.labels?.['agenthub.dev/owner'] === ownerKey('admin'))
const adminObjects = await s3list(`sessions/${ownerKey('admin')}/${adminWork.id}/`)
check('admin session state is in object storage under admin', adminObjects.length > 0, `${adminObjects.length} objects`)

// ---- 3. OIDC ------------------------------------------------------------------------------
await startBackend('oidc')
const T = { root: await signIn('root'), alice: await signIn('alice'), bob: await signIn('bob') }
for (const u of Object.keys(T)) await must(T[u], 'GET', '/api/admin/access')
await must(T.alice, 'PUT', '/api/credentials', { anthropicApiKey: SECRET.alice })
await must(T.alice, 'POST', '/api/projects', { name: 'tools' })
check('a non-admin cannot open the handover', (await api(T.alice, 'GET', '/api/admin/handover')).status === 403)
const overview = await must(T.root, 'GET', '/api/admin/handover')
check('sources are the single-user owners with data', ['admin', 'dev'].every(o => overview.sources.some(s => s.owner === o)),
  overview.sources.map(s => `${s.owner}:${s.sessions}`).join(', '))
check('targets are the signed-in OIDC users only', ['alice', 'bob', 'root'].every(o => overview.targets.some(t => t.owner === o))
  && !overview.targets.some(t => ['admin', 'dev'].includes(t.owner)), overview.targets.map(t => t.owner).join(', '))
check('the API-key name is not a target', (await api(T.root, 'POST', '/api/admin/handover/preview', { from: 'dev', to: 'admin' })).json?.code === 'invalid_target')
check('a user who never signed in is not a target', (await api(T.root, 'POST', '/api/admin/handover/preview', { from: 'dev', to: 'carol' })).json?.code === 'unknown_target')

// A running session blocks.
let devPreview = await must(T.root, 'POST', '/api/admin/handover/preview', { from: 'dev', to: 'bob', includeCredentials: false })
check('a running session blocks the preview', devPreview.liveSessions.includes(devLive.id) && devPreview.token === null)
k('delete', 'pod', `session-${devLive.id}`, '--wait=true')

// ---- 4. Chromium: root hands admin's data to alice, with credentials --------------------------
const browser = await chromium.launch({ executablePath: chrome, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await (await browser.newContext({ viewport: { width: 1440, height: 1100 } })).newPage()
await page.goto(UI + '/settings/users')
await page.getByRole('button', { name: 'Sign in' }).click()
await page.getByRole('link', { name: 'Sign in as root' }).click()
// The callback page finishes the code exchange and leaves; only then is the app signed in.
await page.waitForURL(u => !u.pathname.startsWith('/__identity') && !u.pathname.startsWith('/auth/callback'), { timeout: 30000 })
await page.waitForSelector('.shell', { timeout: 30000 })
await page.goto(UI + '/settings/users')
await page.waitForSelector('[data-handover-from]')
await page.selectOption('[data-handover-from]', 'admin')
await page.selectOption('[data-handover-to]', 'alice')
await page.check('[data-handover-credentials]')
check('the credentials warning shows when ticked', (await page.textContent('[data-handover-credentials-warning]'))
  .includes('The target user will be able to use these logins and keys'))
await page.click('[data-handover-preview]')
await page.waitForSelector('[data-handover-result]')
const previewText = await page.textContent('[data-handover-result]')
await page.locator('[data-handover]').screenshot({ path: path.join(outdir, '1-preview-admin-to-alice.png') })
check('preview names the rename of Tools', previewText.includes('Tools (API key)'))
check('preview shows the stored data size', /\d+ objects, [\d.]+ [KMG]?B/.test(previewText))
check('preview never shows a credential value', !Object.values(SECRET).some(v => previewText.includes(v)) && !(await page.content()).includes(SECRET.admin))
check('preview shows the target keeps its own API key', (await page.textContent('[data-handover-credential="apiKeys"]')).includes("kept by alice's own"))
await page.click('[data-handover-apply]')
await page.waitForSelector('[data-handover-done]', { timeout: 60000 })
await page.locator('[data-handover]').screenshot({ path: path.join(outdir, '2-done-admin-to-alice.png') })
check('Chromium: handed over to alice', (await page.textContent('[data-handover-done]')).includes('Handed over to alice'))

// ---- 5. Chromium again: dev's data to bob, without credentials -----------------------------
// A token previewed before this handover, to show it cannot be applied afterwards.
const earlier = await must(T.root, 'POST', '/api/admin/handover/preview', { from: 'dev', to: 'bob', includeCredentials: false })
check('the card moved on to the next source', await page.inputValue('[data-handover-from]') === 'dev')
check('the credentials tick starts off again', !(await page.isChecked('[data-handover-credentials]')))
await page.selectOption('[data-handover-to]', 'bob')
await page.click('[data-handover-preview]')
await page.waitForSelector('[data-handover-result]')
await page.locator('[data-handover]').screenshot({ path: path.join(outdir, '3-preview-dev-to-bob-without-credentials.png') })
const stays = await page.textContent('[data-handover-credential="gitPats"]')
check('without the tick every kind stays with dev', stays.includes('stays with dev; sessions that need them will ask for new credentials'))
await page.click('[data-handover-apply]')
await page.waitForSelector('[data-handover-done]', { timeout: 60000 })
await page.click('[data-handover-history] summary')
await page.locator('[data-handover]').screenshot({ path: path.join(outdir, '4-done-dev-to-bob-history.png') })
check('Chromium: handed over to bob', (await page.textContent('[data-handover-done]')).includes('Handed over to bob'))
const reuse = await api(T.root, 'POST', '/api/admin/handover/apply', { token: earlier.token })
check('a token previewed before the handover cannot be applied after it', reuse.status === 409, reuse.json?.code)

// ---- 6. what alice has now ------------------------------------------------------------------
const aliceSessions = (await must(T.alice, 'GET', '/api/sessions')).map(s => s.title)
check('alice owns admin\'s sessions', ['admin work', 'admin login', 'nightly'].every(t => aliceSessions.includes(t)), aliceSessions.join(', '))
check('alice does not own dev\'s sessions', !aliceSessions.includes('dev work'))
const aliceProjects = (await must(T.alice, 'GET', '/api/projects')).map(p => p.name)
check('alice keeps her project and gets the renamed one', aliceProjects.includes('tools') && aliceProjects.includes('Tools (API key)'), aliceProjects.join(', '))
const aliceCreds = await must(T.alice, 'GET', '/api/credentials')
check('alice has admin\'s Git token by id', aliceCreds.gitPats.some(p => p.id === adminPat.id), aliceCreds.gitPats.map(p => p.label).join(', '))
check('alice has the Claude login', aliceCreds.claudeSubscription === true)
const aliceSecrets = await must(T.alice, 'GET', '/api/secrets')
check('alice has admin\'s stored secret by id', aliceSecrets.some(s => s.id === adminSecret.id))
check('alice\'s own API key won over admin\'s', secretValue(`creds-${ownerKey('alice')}`, 'anthropic_api_key') === SECRET.alice)
check('admin\'s Git identity moved where alice had none', secretValue(`creds-${ownerKey('alice')}`, 'git_user_name') === 'Admin Proof')
check('admin\'s credential secrets are gone', ['creds', 'usersecrets', 'claude'].every(p => !kjson('get', 'secret', `${p}-${ownerKey('admin')}`)))
// Per moved session, not the whole owner prefix: the bucket may hold an earlier run's sessions.
const adminLeft = []
for (const s of [adminWork, adminLogin, nightly]) adminLeft.push(...await s3list(`sessions/${ownerKey('admin')}/${s.id}/`))
const aliceObjects = await s3list(`sessions/${ownerKey('alice')}/${adminWork.id}/`)
check('object storage: nothing left under admin, state under alice', adminLeft.length === 0 && aliceObjects.length > 0,
  `${adminLeft.length} under admin, ${aliceObjects.length} under alice for ${adminWork.id}`)
const state = await fetch(`${API}/api/sessions/${adminWork.id}/state`, { headers: { Authorization: `Bearer ${T.alice}` } })
check('alice downloads the moved state archive through the backend', state.ok && (await state.arrayBuffer()).byteLength > 0, `status ${state.status}`)
const cron = kjson('get', 'cronjob', `session-${nightly.id}`)
const cronRefs = JSON.stringify(cron?.spec?.jobTemplate?.spec?.template?.spec ?? {})
check('the schedule was rebuilt for alice', cron?.metadata?.labels?.['agenthub.dev/owner'] === ownerKey('alice')
  && !cron.metadata.annotations?.['agenthub.dev/handover-suspend'] && cron.spec.suspend !== true
  && cronRefs.includes(ownerKey('alice')) && !cronRefs.includes(ownerKey('admin')))

// ---- 7. what bob has now (no credentials ticked) --------------------------------------------
const bobSessions = (await must(T.bob, 'GET', '/api/sessions')).map(s => s.title)
check('bob owns dev\'s sessions', bobSessions.includes('dev work') && bobSessions.includes('dev live'), bobSessions.join(', '))
const bobCreds = await must(T.bob, 'GET', '/api/credentials')
check('bob got no stored credentials', !bobCreds.anthropicApiKey && bobCreds.gitPats.length === 0 && (await must(T.bob, 'GET', '/api/secrets')).length === 0)
check('dev\'s credentials stay with dev', secretValue(`creds-${ownerKey('dev')}`, 'anthropic_api_key') === SECRET.dev
  && !!kjson('get', 'secret', `usersecrets-${ownerKey('dev')}`))
const resume = await api(T.bob, 'POST', `/api/sessions/${devWork.id}/resume`)
const resumed = await must(T.bob, 'GET', `/api/sessions/${devWork.id}`)
const pod = kjson('get', 'pod', `session-${devWork.id}`)
const podEnv = JSON.stringify(pod?.spec?.containers ?? [])
results.push(`INFO resume of a moved session without credentials: HTTP ${resume.status}, phase ${resumed.phase}, failure ${JSON.stringify(resumed.failureReason ?? null)}, pod ${pod ? 'created' : 'none'}`)
console.log(results.at(-1))
check('the resumed session does not reach dev\'s credentials', !podEnv.includes(ownerKey('dev')) && !podEnv.includes(devSecret.id) && !podEnv.includes(devPat.id))

// ---- 8. audit -------------------------------------------------------------------------------
const history = (await must(T.root, 'GET', '/api/admin/handover')).history
check('audit: two completed handovers by root', history.length === 2 && history.every(h => h.admin === 'root' && h.state === 'completed'),
  history.map(h => `${h.from}->${h.to} credentials=${h.credentials} sessions=${h.counts.sessions}`).join('; '))
const after = await must(T.root, 'GET', '/api/admin/handover')
check('nothing from API-key mode is left to hand over', after.sources.length === 0)

await browser.close()
await db.end()
fs.writeFileSync(path.join(outdir, 'results.txt'), results.join('\n') + '\n')
console.log(failed ? `${failed} FAILED` : 'ALL PASS')
process.exit(failed ? 1 : 0)
