// Stand-in GitLab for #289: OAuth authorize/token, the two API calls the hub and glab make, and a
// private repository over real git smart HTTP (git http-backend). Behaviour copied from GitLab:
// - access tokens expire after ACCESS_TTL seconds; a refresh rotates the refresh token, and a spent
//   one is answered 400 invalid_grant. The previous access token is revoked by the refresh (the
//   strictest reading; the hub must hand the new one out at once either way).
// - an expired or unknown token is 401 on git ("HTTP Basic: Access denied") and on the API
//   (invalid_token), as measured against gitlab.com with an invalid token.
// - the failed-authentication ban for git over HTTP (gitlab.yml rack_attack.git_basic_auth):
//   after BAN_RETRY failed logins within BAN_FIND seconds from one address, that address gets 403
//   for BAN_TIME seconds, valid token or not. Every session pod leaves the cluster from the same
//   address, so one session retrying a dead token bans all of them.
// Tokens never reach the log or any answer except the token response itself: events name tokens
// by a label (at#3, rt#4).
// Run: node mock-gitlab.mjs <dir> > <dir>/events.log   (dir: certs/, repos/, client-secret.txt)
import { spawn } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import https from 'node:https'
import path from 'node:path'

const dir = process.argv[2]
const HOST = process.env.MOCK_HOST || '127.0.0.10'
const PORT = Number(process.env.MOCK_PORT || 443)
const ACCESS_TTL = Number(process.env.ACCESS_TTL || 60)
const TOKEN_DELAY_MS = Number(process.env.TOKEN_DELAY_MS || 300)
const BAN_RETRY = Number(process.env.BAN_RETRY || 10), BAN_FIND = Number(process.env.BAN_FIND || 60), BAN_TIME = Number(process.env.BAN_TIME || 3600)
const CLIENT_ID = 'proof-client', CLIENT_SECRET = fs.readFileSync(path.join(dir, 'client-secret.txt'), 'utf8').trim()

const access = new Map()   // token -> { label, expires, revoked }
const refresh = new Map()  // token -> { label, live, access }
const codes = new Map()
let counter = 0
const stats = { authorizationCodes: 0, refreshes: 0, invalidGrants: 0, git: { ok: 0, 401: 0, 403: 0 }, api: { ok: 0, 401: 0 } }
const failures = new Map() // ip -> [timestamps]
const banned = new Map()   // ip -> until

// Events go to stdout (run.sh keeps them in events.log): a test server has no business writing
// request-derived text into files of its own.
const log = m => console.log(`${new Date().toISOString()} ${m}`)
const now = () => Date.now() / 1000

function issue(reason) {
  const n = ++counter
  const at = crypto.randomBytes(24).toString('hex'), rt = crypto.randomBytes(24).toString('hex')
  access.set(at, { label: `at#${n}`, expires: now() + ACCESS_TTL, revoked: false })
  refresh.set(rt, { label: `rt#${n}`, live: true, access: at })
  log(`token issued (${reason}): at#${n} rt#${n}, expires in ${ACCESS_TTL}s`)
  return { access_token: at, token_type: 'Bearer', expires_in: ACCESS_TTL, refresh_token: rt, scope: 'api read_user', created_at: Math.floor(now()) }
}

function check(token) {
  const t = token && access.get(token)
  if (!t) return { ok: false, why: 'unknown' }
  if (t.revoked) return { ok: false, why: `${t.label} revoked` }
  if (t.expires < now()) return { ok: false, why: `${t.label} expired` }
  return { ok: true, label: t.label }
}

function body(req) {
  return new Promise(resolve => { const c = []; req.on('data', d => c.push(d)); req.on('end', () => resolve(Buffer.concat(c))) })
}
const json = (res, status, obj, headers = {}) => { res.writeHead(status, { 'Content-Type': 'application/json', ...headers }); res.end(JSON.stringify(obj)) }

function gitFailure(ip) {
  const list = (failures.get(ip) || []).filter(t => t > now() - BAN_FIND)
  list.push(now())
  failures.set(ip, list)
  if (list.length >= BAN_RETRY) { banned.set(ip, now() + BAN_TIME); log(`ban ${ip} for ${BAN_TIME}s after ${list.length} failed git logins`) }
}

async function handle(req, res) {
  const url = new URL(req.url, `https://${HOST}`)
  const ip = req.socket.remoteAddress

  if (url.pathname === '/mock/stats') return json(res, 200, stats)
  if (url.pathname === '/mock/revoke' && req.method === 'POST') {
    for (const t of access.values()) t.revoked = true
    for (const t of refresh.values()) t.live = false
    log('owner revoked the application: every token is dead')
    return json(res, 200, { revoked: true })
  }
  if (url.pathname === '/mock/unban' && req.method === 'POST') { banned.clear(); failures.clear(); return json(res, 200, {}) }

  if (url.pathname === '/oauth/authorize') {
    const code = crypto.randomBytes(16).toString('hex')
    codes.set(code, url.searchParams.get('redirect_uri'))
    const target = new URL(url.searchParams.get('redirect_uri'))
    target.searchParams.set('code', code)
    target.searchParams.set('state', url.searchParams.get('state'))
    res.writeHead(302, { Location: target.toString() })
    return res.end()
  }

  if (url.pathname === '/oauth/token' && req.method === 'POST') {
    const form = new URLSearchParams((await body(req)).toString())
    // A slow answer widens the window in which a second caller could redeem the same token.
    await new Promise(r => setTimeout(r, TOKEN_DELAY_MS))
    if (form.get('client_id') !== CLIENT_ID || form.get('client_secret') !== CLIENT_SECRET)
      return json(res, 401, { error: 'invalid_client' })
    if (form.get('grant_type') === 'authorization_code') {
      if (!codes.delete(form.get('code'))) return json(res, 400, { error: 'invalid_grant' })
      stats.authorizationCodes++
      return json(res, 200, issue('authorization_code'))
    }
    if (form.get('grant_type') === 'refresh_token') {
      const r = refresh.get(form.get('refresh_token'))
      if (!r || !r.live) {
        stats.invalidGrants++
        log(`refresh with ${r ? r.label + ' (already spent or revoked)' : 'an unknown token'} -> 400 invalid_grant`)
        return json(res, 400, { error: 'invalid_grant', error_description: 'The provided authorization grant is invalid, expired, revoked, does not match the redirection URI used in the authorization request, or was issued to another client.' })
      }
      r.live = false
      const old = access.get(r.access); if (old) old.revoked = true
      stats.refreshes++
      log(`refresh with ${r.label} -> ok, ${r.label} and ${old?.label} now dead`)
      return json(res, 200, issue('refresh_token'))
    }
    return json(res, 400, { error: 'unsupported_grant_type' })
  }

  if (url.pathname.startsWith('/api/v4/')) {
    const bearer = (req.headers.authorization || '').replace(/^Bearer /, '') || req.headers['private-token']
    const c = check(bearer)
    if (!c.ok) {
      stats.api[401]++
      log(`api ${url.pathname} -> 401 (${c.why})`)
      return json(res, 401, { error: 'invalid_token', error_description: 'Token is expired. You can either do re-authorization or token refresh.' },
        { 'WWW-Authenticate': 'Bearer realm="Protected by OAuth 2.0", error="invalid_token"' })
    }
    stats.api.ok++
    if (url.pathname === '/api/v4/user') return json(res, 200, { id: 1, username: 'proof-owner' })
    if (url.pathname === '/api/v4/projects') return json(res, 200, [{ path: 'project', path_with_namespace: 'group/project', http_url_to_repo: `https://${HOST}/group/project.git`, default_branch: 'main' }])
    return json(res, 404, { message: '404 Not Found' })
  }

  // git smart HTTP on a private project.
  if (url.pathname.startsWith('/group/project.git/')) {
    if ((banned.get(ip) || 0) > now()) {
      stats.git[403]++
      log(`git ${req.method} ${url.pathname} from ${ip} -> 403 (banned)`)
      res.writeHead(403, { 'Content-Type': 'text/plain' })
      return res.end('Forbidden\n')
    }
    const auth = req.headers.authorization
    const [user, password] = auth?.startsWith('Basic ') ? Buffer.from(auth.slice(6), 'base64').toString().split(':') : []
    const c = check(password)
    if (!auth || user !== 'oauth2' || !c.ok) {
      stats.git[401]++
      if (auth) gitFailure(ip)
      log(`git ${req.method} ${url.pathname}${url.search} -> 401 (${auth ? c.why : 'no credentials'})`)
      res.writeHead(401, { 'Content-Type': 'text/plain', 'WWW-Authenticate': 'Basic realm="GitLab"' })
      return res.end("HTTP Basic: Access denied. If a password was provided for Git authentication, the password was incorrect or you're required to use a token instead of a password. If a token was provided, it was either incorrect, expired, or improperly scoped.\n")
    }
    stats.git.ok++
    log(`git ${req.method} ${url.pathname}${url.search} -> ok with ${c.label}`)
    return cgi(req, res, url)
  }
  res.writeHead(404); res.end()
}

function cgi(req, res, url) {
  const child = spawn('git', ['http-backend'], {
    env: {
      PATH: process.env.PATH, GIT_PROJECT_ROOT: path.join(dir, 'repos'), GIT_HTTP_EXPORT_ALL: '1', REMOTE_USER: 'proof-owner',
      PATH_INFO: url.pathname, QUERY_STRING: url.search.slice(1), REQUEST_METHOD: req.method,
      CONTENT_TYPE: req.headers['content-type'] || '', CONTENT_LENGTH: req.headers['content-length'] || '',
      HTTP_CONTENT_ENCODING: req.headers['content-encoding'] || '', GIT_PROTOCOL: req.headers['git-protocol'] || ''
    }
  })
  req.pipe(child.stdin)
  let head = Buffer.alloc(0), sent = false
  child.stdout.on('data', chunk => {
    if (sent) return res.write(chunk)
    head = Buffer.concat([head, chunk])
    const end = head.indexOf('\r\n\r\n')
    if (end < 0) return
    const headers = {}; let status = 200
    for (const line of head.slice(0, end).toString().split('\r\n')) {
      const i = line.indexOf(':'); const k = line.slice(0, i).trim(), v = line.slice(i + 1).trim()
      if (k.toLowerCase() === 'status') status = parseInt(v); else headers[k] = v
    }
    res.writeHead(status, headers); sent = true
    res.write(head.slice(end + 4))
  })
  child.stdout.on('end', () => res.end())
  child.stderr.on('data', d => log('http-backend: ' + d.toString().trim()))
}

https.createServer({ key: fs.readFileSync(path.join(dir, 'certs/server.key')), cert: fs.readFileSync(path.join(dir, 'certs/server.crt')) },
  (req, res) => handle(req, res).catch(e => { log('error ' + e.message); res.writeHead(500); res.end() }))
  .listen(PORT, HOST, () => log(`mock GitLab on https://${HOST}:${PORT}, access tokens live ${ACCESS_TTL}s`))
