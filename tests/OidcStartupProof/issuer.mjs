import http from 'node:http'
import https from 'node:https'
import { readFileSync } from 'node:fs'
import { generateKeyPairSync, createHash, randomBytes, sign } from 'node:crypto'

// A local public-client issuer exercises PKCE, offline scope and JWT validation without
// storing a real user's identity-service credentials in an install proof.
const issuer = process.env.PROOF_ISSUER
if (!issuer) throw new Error('PROOF_ISSUER is required')
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 })
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'install-proof', use: 'sig', alg: 'RS256' }
const codes = new Map(), refreshes = new Set()
const token = () => {
  const header = Buffer.from(JSON.stringify({ alg: 'RS256', kid: jwk.kid })).toString('base64url')
  const now = Math.floor(Date.now() / 1000)
  const payload = Buffer.from(JSON.stringify({ iss: issuer, aud: 'agenthub', sub: 'team-admin', preferred_username: 'team-admin', iat: now, exp: now + 300 })).toString('base64url')
  const input = `${header}.${payload}`
  return `${input}.${sign('sha256', Buffer.from(input), privateKey).toString('base64url')}`
}
const server = process.env.PROOF_TLS_CERT
  ? handler => https.createServer({ cert: readFileSync(process.env.PROOF_TLS_CERT), key: readFileSync(process.env.PROOF_TLS_KEY) }, handler)
  : handler => http.createServer(handler)
server(async (req, res) => {
  const url = new URL(req.url, issuer)
  res.setHeader('Access-Control-Allow-Origin', 'http://127.0.0.1:18080')
  res.setHeader('Content-Type', 'application/json')
  const json = value => res.end(JSON.stringify(value))
  if (url.pathname === '/.well-known/openid-configuration') return json({ issuer, jwks_uri: `${issuer}/keys`, authorization_endpoint: `${issuer}/authorize`, token_endpoint: `${issuer}/token`, response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'], scopes_supported: ['openid', 'profile', 'email', 'offline_access'], code_challenge_methods_supported: ['S256'] })
  if (url.pathname === '/keys') return json({ keys: [jwk] })
  if (url.pathname === '/authorize') {
    const q = url.searchParams
    if (q.get('client_id') !== 'agenthub' || q.get('redirect_uri') !== 'http://127.0.0.1:18080/auth/callback' || q.get('code_challenge_method') !== 'S256' || !q.get('scope')?.split(' ').includes('offline_access')) { res.statusCode = 400; return json({ error: 'invalid_request' }) }
    const code = randomBytes(24).toString('hex')
    codes.set(code, { challenge: q.get('code_challenge'), redirect: q.get('redirect_uri') })
    const redirect = new URL(q.get('redirect_uri')); redirect.searchParams.set('code', code); redirect.searchParams.set('state', q.get('state'))
    res.writeHead(302, { Location: redirect.href }); return res.end()
  }
  if (url.pathname === '/token' && req.method === 'POST') {
    let body = ''; for await (const chunk of req) body += chunk
    const q = new URLSearchParams(body)
    let allowed = false
    if (q.get('grant_type') === 'authorization_code') {
      const code = codes.get(q.get('code')); codes.delete(q.get('code'))
      allowed = code && code.redirect === q.get('redirect_uri') && code.challenge === createHash('sha256').update(q.get('code_verifier') || '').digest('base64url')
    } else if (q.get('grant_type') === 'refresh_token') {
      allowed = refreshes.delete(q.get('refresh_token'))
    }
    if (!allowed || q.get('client_id') !== 'agenthub') { res.statusCode = 400; return json({ error: 'invalid_grant' }) }
    const refresh = randomBytes(24).toString('hex'); refreshes.add(refresh)
    return json({ access_token: token(), refresh_token: refresh, token_type: 'Bearer', expires_in: 300 })
  }
  res.statusCode = 404; json({ error: 'not_found' })
}).listen(Number(new URL(issuer).port), '0.0.0.0', () => console.log('Local proof issuer listening'))
