// #492 proof against the REAL backend in OIDC mode: real JWT validation (discovery + JWKS over
// HTTP from a local issuer), real controllers, real Postgres. See README.md.
//   node proof.mjs <branch AgentHub.Api.dll> [baseline AgentHub.Api.dll]
import { execFileSync, spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import http from 'node:http'
import os from 'node:os'
import path from 'node:path'

const [branchDll, baselineDll] = process.argv.slice(2)
if (!branchDll) { console.error('usage: node proof.mjs <branch dll> [baseline dll]'); process.exit(2) }
const ISSUER = 'http://127.0.0.1:9492'
const API = 'http://127.0.0.1:8492'
// Generated per run and never printed: the API-key scenario only needs some valid key.
const API_KEY = crypto.randomBytes(24).toString('hex')
const DB = process.env.PROOF_DB || 'Host=127.0.0.1;Port=54329;Username=postgres;Database=proof492'

// --- A minimal OIDC issuer: discovery and JWKS, tokens signed with a key that exists only here.
const { privateKey, publicKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'proof', use: 'sig', alg: 'RS256' }
const issuer = http.createServer((req, res) => {
  const body = req.url === '/.well-known/openid-configuration'
    ? { issuer: ISSUER, jwks_uri: `${ISSUER}/jwks`, authorization_endpoint: `${ISSUER}/auth`, token_endpoint: `${ISSUER}/token`,
        response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'] }
    : req.url === '/jwks' ? { keys: [jwk] } : null
  res.writeHead(body ? 200 : 404, { 'content-type': 'application/json' }).end(JSON.stringify(body ?? {}))
})
await new Promise(r => issuer.listen(9492, '127.0.0.1', r))

const b64 = o => Buffer.from(JSON.stringify(o)).toString('base64url')
function token(user) {
  const now = Math.floor(Date.now() / 1000)
  const head = b64({ alg: 'RS256', typ: 'JWT', kid: 'proof' })
  const claims = b64({ iss: ISSUER, sub: `sub-${user}`, preferred_username: user, email: `${user}@example.com`,
    name: user, iat: now, nbf: now - 5, exp: now + 3600 })
  const sig = crypto.sign('RSA-SHA256', Buffer.from(`${head}.${claims}`), privateKey).toString('base64url')
  return `${head}.${claims}.${sig}`
}

// The backend reads a kubeconfig at startup; nothing here needs a cluster, so point it nowhere.
// Some clients read $HOME/.kube/config regardless of KUBECONFIG, so the backend gets its own HOME.
const kubeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'proof492-'))
fs.mkdirSync(path.join(kubeDir, '.kube'))
fs.writeFileSync(path.join(kubeDir, '.kube', 'config'), `apiVersion: v1
kind: Config
clusters: [{name: none, cluster: {server: "https://127.0.0.1:1"}}]
users: [{name: none, user: {token: unused}}]
contexts: [{name: none, context: {cluster: none, user: none}}]
current-context: none
`)

// The backend refuses to start without a data-protection certificate; a throwaway one will do.
const dpCert = path.join(kubeDir, 'dp-cert.pem'), dpKey = path.join(kubeDir, 'dp-key.pem')
execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=proof492',
  '-keyout', dpKey, '-out', dpCert], { stdio: 'ignore' })

function start(dll, admins, log, mode = 'oidc') {
  const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', ASPNETCORE_URLS: API,
    HOME: kubeDir, KUBECONFIG: path.join(kubeDir, '.kube', 'config'),
    ...(mode === 'oidc' ? { Oidc__Authority: ISSUER, Oidc__RequireHttpsMetadata: 'false' }
      : { Auth__ApiKey: API_KEY, Auth__ApiKeyOwner: 'keyholder' }),
    ConnectionStrings__Postgres: DB, Ee__Admins: admins,
    AgentHub__Namespace: 'agenthub-sessions', AgentHub__CallbackBaseUrl: API,
    AgentHub__ClaudeAgentImage: 'registry.example.com/agent-claude:proof',
    AgentHub__AgentImage: 'registry.example.com/agent:proof', Browser__Enabled: 'false',
    DataProtection__CertificatePem: fs.readFileSync(dpCert, 'utf8'), DataProtection__PrivateKeyPem: fs.readFileSync(dpKey, 'utf8') }
  for (const k of ['Auth__InsecureDevMode', ...(mode === 'oidc' ? ['Auth__ApiKey', 'Auth__ApiKeyOwner'] : ['Oidc__Authority'])])
    delete env[k]
  const out = fs.openSync(log, 'w')
  return spawn(process.env.DOTNET || 'dotnet', [dll], { env, stdio: ['ignore', out, out] })
}
async function healthy(child) {
  for (let i = 0; i < 180; i++) {
    // An unhandled exception aborts the process: exitCode stays null and signalCode is set.
    if (exited(child)) throw new Error(`backend exited ${child.exitCode ?? child.signalCode}`)
    try { if ((await fetch(`${API}/healthz`)).ok) return } catch { }
    await new Promise(r => setTimeout(r, 500))
  }
  throw new Error('backend not healthy')
}
const exited = child => child.exitCode !== null || child.signalCode !== null
async function stop(child) {
  if (exited(child)) return
  const gone = new Promise(r => child.once('exit', r))
  child.kill('SIGTERM')
  await gone
}
const call = async (user, method, p, body) => {
  const res = await fetch(API + p, { method, headers: { authorization: `Bearer ${token(user)}`,
    ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined })
  let json = null; try { json = await res.json() } catch { }
  return { status: res.status, json }
}
let failed = 0
const check = (name, ok, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ` — ${detail}` : ''}`); if (!ok) failed++ }
const logDir = process.env.PROOF_LOG_DIR || kubeDir

async function scenario(label, dll, admins, steps, mode = 'oidc') {
  console.log(`\n== ${label} (${mode}, Ee:Admins="${admins}")`)
  const log = path.join(logDir, `${label.replace(/\W+/g, '-')}.log`)
  const child = start(dll, admins, log, mode)
  try { await healthy(child); await steps(log) } finally { await stop(child) }
}

if (baselineDll) {
  await scenario('baseline main, nobody designated', baselineDll, '', async () => {
    const a = await call('first-user', 'GET', '/api/admin/access')
    console.log(`  first-user /api/admin/access: ${a.status} ${JSON.stringify(a.json)}`)
    const r = await call('first-user', 'GET', '/api/admin/roles')
    console.log(`  first-user GET /api/admin/roles: ${r.status}`)
    check('baseline reproduces the defect: an undesignated OIDC user is admin', a.json?.isAdmin === true && r.status === 200)
  })
}

await scenario('branch, nobody designated', branchDll, '', async () => {
  for (const u of ['first-user', 'second-user']) {
    const a = await call(u, 'GET', '/api/admin/access')
    console.log(`  ${u} /api/admin/access: ${a.status} ${JSON.stringify(a.json)}`)
    check(`${u} is not admin, adminDesignated=false`, a.status === 200 && a.json?.isAdmin === false && a.json?.adminDesignated === false)
    const r = await call(u, 'GET', '/api/admin/roles')
    check(`${u} GET /api/admin/roles is refused`, r.status === 403, `status ${r.status}`)
    const p = await call(u, 'PUT', `/api/admin/users/${u}/role`, { role: 'admin' })
    check(`${u} cannot make themselves admin`, p.status === 403, `status ${p.status}`)
  }
})

await scenario('branch, alice configured', branchDll, 'alice', async () => {
  const alice = await call('alice', 'GET', '/api/admin/access')
  console.log(`  alice /api/admin/access: ${JSON.stringify(alice.json)}`)
  check('configured admin alice is admin', alice.json?.isAdmin === true && alice.json?.adminDesignated === true)
  const bob = await call('bob', 'GET', '/api/admin/access')
  check('bob is not admin, and sees no notice', bob.json?.isAdmin === false && bob.json?.adminDesignated === true)
  const promote = await call('alice', 'PUT', '/api/admin/users/bob/role', { role: 'admin' })
  const row = promote.json?.users?.find(u => u.owner === 'bob')
  check('alice promotes bob in the admin area', promote.status === 200 && row?.role === 'admin' && row?.source === 'user',
    `status ${promote.status}, bob ${JSON.stringify(row && { role: row.role, source: row.source })}`)
  check('the roles overview no longer carries a bootstrap flag', promote.json && !('bootstrap' in promote.json))
})

await scenario('branch, existing install: ee.admins removed, bob kept in the database', branchDll, '', async () => {
  const bob = await call('bob', 'GET', '/api/admin/access')
  check('explicitly designated bob stays admin across restarts', bob.json?.isAdmin === true && bob.json?.adminDesignated === true)
  const alice = await call('alice', 'GET', '/api/admin/access')
  check('alice, no longer in ee.admins and never set in the UI, is a user', alice.json?.isAdmin === false)
  const carol = await call('carol', 'GET', '/api/admin/access')
  check('a new user is not admin', carol.json?.isAdmin === false)
  const demote = await call('bob', 'PUT', '/api/admin/users/bob/role', { role: 'user' })
  check('bob cannot demote the last admin (himself)', demote.status === 409, `status ${demote.status}`)
})

// Switch from the API key to OIDC (#492): the key acted as "keyholder" and created a project.
await scenario('branch, API-key mode as keyholder', branchDll, '', async () => {
  const res = await fetch(`${API}/api/projects`, { method: 'POST',
    headers: { authorization: `Bearer ${API_KEY}`, 'content-type': 'application/json' },
    body: JSON.stringify({ name: 'made with the key' }) })
  check('the key holder creates a project', res.ok, `status ${res.status}`)
}, 'apiKey')

await scenario('branch, switched to OIDC, bob designated', branchDll, 'bob', async log => {
  const warned = fs.readFileSync(log, 'utf8').includes('Data from API-key mode is under the name keyholder (0 session(s), 1 project(s))')
  check('startup log warns about data under the old key owner name', warned)
  const admin = await call('bob', 'GET', '/api/admin/access')
  console.log(`  bob /api/admin/access: ${JSON.stringify(admin.json)}`)
  check('the admin is told which name holds API-key data',
    JSON.stringify(admin.json?.singleUserOwnerData) === JSON.stringify([{ owner: 'keyholder', sessions: 0, projects: 1 }]))
  const holder = await call('keyholder', 'GET', '/api/admin/access')
  console.log(`  keyholder (IdP user) /api/admin/access: ${JSON.stringify(holder.json)}`)
  check('the IdP user with the old owner name is not admin and gets no data counts',
    holder.json?.isAdmin === false && holder.json?.singleUserOwnerData?.length === 0)
  const promote = await call('bob', 'PUT', '/api/admin/users/keyholder/role', { role: 'admin' })
  check('promoting the old owner name in the admin area is refused', promote.status === 409,
    `status ${promote.status}: ${promote.json?.error}`)
  const after = await call('keyholder', 'GET', '/api/admin/access')
  check('it stays a regular user', after.json?.isAdmin === false)
})

issuer.close()
console.log(`\n${failed ? `${failed} FAILED` : 'ALL PASSED'}`)
process.exit(failed ? 1 : 0)
