// Local IdP and TLS ingress stand-ins; the backend's OAuth and MCP handlers stay production code.
import http from 'node:http'
import https from 'node:https'
import fs from 'node:fs'
import crypto from 'node:crypto'
const dir = process.env.PROOF_DIR
if (!dir) throw new Error('Set PROOF_DIR to a temporary directory with certs/server.key and server.crt')
const origin = 'https://127.0.0.1:18443'
const issuer = origin + '/oidc'
const key = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
const jwk = { ...key.publicKey.export({ format: 'jwk' }), kid: 'proof', alg: 'RS256', use: 'sig' }
const codes = new Map()
const json = (res, value) => { res.setHeader('content-type', 'application/json'); res.end(JSON.stringify(value)) }
const body = req => new Promise(resolve => { let data = ''; req.on('data', c => { data += c }); req.on('end', () => resolve(new URLSearchParams(data))) })
const jwt = payload => {
  const input = [Buffer.from(JSON.stringify({ alg: 'RS256', kid: 'proof' })).toString('base64url'), Buffer.from(JSON.stringify(payload)).toString('base64url')].join('.')
  return input + '.' + crypto.sign('RSA-SHA256', Buffer.from(input), key.privateKey).toString('base64url')
}
https.createServer({ key: fs.readFileSync(`${dir}/certs/server.key`), cert: fs.readFileSync(`${dir}/certs/server.crt`) }, async (req, res) => {
  const url = new URL(req.url, origin)
  if (url.pathname === '/oidc/.well-known/openid-configuration') return json(res, {
    issuer, authorization_endpoint: issuer + '/authorize', token_endpoint: issuer + '/token', jwks_uri: issuer + '/jwks',
    response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'],
    scopes_supported: ['openid', 'profile', 'email'], token_endpoint_auth_methods_supported: ['none'], code_challenge_methods_supported: ['S256']
  })
  if (url.pathname === '/oidc/jwks') return json(res, { keys: [jwk] })
  if (url.pathname === '/oidc/authorize') {
    if (req.method === 'GET') {
      res.setHeader('content-type', 'text/html')
      res.end('<form method="post"><label>Proof user <input name="username" required></label><button>Sign in</button></form>')
      return
    }
    const form = await body(req)
    if (form.get('username') !== 'alice') { res.writeHead(403); res.end(); return }
    const code = crypto.randomBytes(24).toString('hex')
    codes.set(code, Object.fromEntries(url.searchParams))
    const target = new URL(url.searchParams.get('redirect_uri'))
    target.searchParams.set('code', code); target.searchParams.set('state', url.searchParams.get('state'))
    res.writeHead(302, { location: target.href }); res.end(); return
  }
  if (url.pathname === '/oidc/token') {
    const form = await body(req), auth = codes.get(form.get('code'))
    codes.delete(form.get('code'))
    if (!auth || crypto.createHash('sha256').update(form.get('code_verifier') || '').digest('base64url') !== auth.code_challenge
      || form.get('redirect_uri') !== auth.redirect_uri) { res.writeHead(400); return json(res, { error: 'invalid_grant' }) }
    const now = Math.floor(Date.now() / 1000)
    json(res, { token_type: 'Bearer', expires_in: 300, access_token: 'synthetic-idp-access', id_token: jwt({ iss: issuer, sub: 'alice',
      aud: auth.client_id, nonce: auth.nonce, iat: now, exp: now + 300, preferred_username: 'alice' }) }); return
  }
  const upstream = http.request({ hostname: '127.0.0.1', port: 18082, path: req.url, method: req.method,
    headers: { ...req.headers, 'x-forwarded-proto': 'https' } }, response => { res.writeHead(response.statusCode, response.headers); response.pipe(res) })
  upstream.on('error', () => { res.writeHead(502); res.end() })
  req.pipe(upstream)
}).listen(18443, '127.0.0.1')
http.createServer((req, res) => {
  const path = req.url.split('?')[0]
  json(res, { apiVersion: 'v1', kind: path.includes('cronjobs') ? 'CronJobList' : path.includes('secrets') ? 'SecretList' : 'PodList', metadata: {}, items: [] })
}).listen(18081, '127.0.0.1')
console.log('Proof TLS ingress, OIDC provider and empty Kubernetes API listening')
