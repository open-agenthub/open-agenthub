import { randomBytes, createHash } from 'node:crypto'
const verifier = randomBytes(32).toString('base64url')
const url = new URL('/authorize', process.env.PROOF_ISSUER)
url.search = new URLSearchParams({ client_id: 'agenthub', redirect_uri: 'http://127.0.0.1:18080/auth/callback', response_type: 'code', scope: 'openid profile email offline_access', state: 'proof', code_challenge_method: 'S256', code_challenge: createHash('sha256').update(verifier).digest('base64url') })
const authorization = await fetch(url, { redirect: 'manual' })
if (authorization.status !== 302) throw new Error('Authorization failed')
const redirect = new URL(authorization.headers.get('location'))
if (redirect.searchParams.get('state') !== 'proof') throw new Error('State mismatch')
async function exchange(body) {
  const response = await fetch(new URL('/token', process.env.PROOF_ISSUER), { method: 'POST', body: new URLSearchParams({ client_id: 'agenthub', ...body }) })
  if (!response.ok) throw new Error(`Token exchange failed: ${response.status}`)
  return response.json()
}
let tokens = await exchange({ grant_type: 'authorization_code', code: redirect.searchParams.get('code'), code_verifier: verifier, redirect_uri: 'http://127.0.0.1:18080/auth/callback' })
if (!tokens.refresh_token) throw new Error('Missing offline refresh token')
tokens = await exchange({ grant_type: 'refresh_token', refresh_token: tokens.refresh_token })
if (process.env.PROOF_PROTOCOL_ONLY === 'true') {
  console.log('PASS: local PKCE authorization-code exchange and offline refresh');
  process.exit(0)
}
const response = await fetch(`${process.env.PROOF_API || 'http://127.0.0.1:18080'}/api/admin/overview`, { headers: { Authorization: `Bearer ${tokens.access_token}` } })
if (!response.ok || !(await response.json()).isAdmin) throw new Error(`Team admin request failed: ${response.status}`)
console.log('PASS: quickstart upgraded to OIDC, PKCE code and offline refresh exchanged, real API authenticated team admin')
