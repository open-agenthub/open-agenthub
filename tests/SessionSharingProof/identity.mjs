// Local protocol fixture: real signed tokens and PKCE exercise the app's OIDC redirect and
// the backend JWT handler without requiring access to a deployed identity provider.
import http from 'node:http'
import { generateKeyPairSync, randomBytes, createHash, createSign } from 'node:crypto'
const issuer = process.env.PROOF_ISSUER
if (!issuer) throw new Error('Set PROOF_ISSUER to this fixture’s browser-reachable origin')
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 })
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'proof', use: 'sig', alg: 'RS256' }
const grants = new Map()
const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url')
function token(claims) {
  const payload = encode({ alg: 'RS256', kid: 'proof' }) + '.' + encode(claims)
  return payload + '.' + createSign('RSA-SHA256').update(payload).sign(privateKey).toString('base64url')
}
http.createServer(async (req, res) => {
  res.setHeader('Access-Control-Allow-Origin', process.env.UI)
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type')
  if (req.method === 'OPTIONS') { res.writeHead(204).end(); return }
  const url = new URL(issuer + req.url)
  const route = url.pathname.replace(new URL(issuer).pathname.replace(/\/$/, ''), '') || '/'
  if (route === '/.well-known/openid-configuration') {
    res.setHeader('Content-Type', 'application/json')
    res.end(JSON.stringify({ issuer, authorization_endpoint: issuer + '/authorize', token_endpoint: issuer + '/token', jwks_uri: issuer + '/keys', response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'], token_endpoint_auth_methods_supported: ['none'], code_challenge_methods_supported: ['S256'] })); return
  }
  if (route === '/keys') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ keys: [jwk] })); return }
  if (route === '/authorize') {
    const user = url.searchParams.get('user')
    if (!['alice', 'bob'].includes(user)) {
      res.setHeader('Content-Type', 'text/html')
      res.end('<h1>Proof sign in</h1>' + ['alice', 'bob'].map(user => { const next = new URL(url); next.searchParams.set('user', user); return `<p><a href="${next.pathname + next.search}">Sign in as ${user}</a></p>` }).join('')); return
    }
    const code = randomBytes(24).toString('hex')
    grants.set(code, { user, challenge: url.searchParams.get('code_challenge'), nonce: url.searchParams.get('nonce'), redirect: url.searchParams.get('redirect_uri') })
    const redirect = new URL(url.searchParams.get('redirect_uri')); redirect.searchParams.set('code', code); redirect.searchParams.set('state', url.searchParams.get('state'))
    res.writeHead(302, { Location: redirect.href }).end(); return
  }
  if (route === '/token') {
    let body = ''; for await (const chunk of req) body += chunk
    const params = new URLSearchParams(body); const grant = grants.get(params.get('code')); grants.delete(params.get('code'))
    if (!grant || grant.redirect !== params.get('redirect_uri') || createHash('sha256').update(params.get('code_verifier') || '').digest('base64url') !== grant.challenge) { res.writeHead(400).end(); return }
    const now = Math.floor(Date.now() / 1000)
    const claims = { iss: issuer, aud: 'proof328', sub: grant.user, preferred_username: grant.user, name: grant.user, iat: now, exp: now + 3600 }
    res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ access_token: token(claims), id_token: token({ ...claims, nonce: grant.nonce }), token_type: 'Bearer', expires_in: 3600, scope: 'openid profile' })); return
  }
  res.writeHead(404).end()
}).listen(8090, '0.0.0.0', () => console.log('Local identity fixture ready'))
