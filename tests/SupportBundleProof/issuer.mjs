import http from 'node:http';
import { generateKeyPairSync, sign } from 'node:crypto';
const issuer = process.env.PROOF_ISSUER;
if (!issuer) throw Error('PROOF_ISSUER required');
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'support-proof', use: 'sig', alg: 'RS256' };
const token = user => {
  const now = Math.floor(Date.now() / 1000);
  const input = Buffer.from(JSON.stringify({ alg: 'RS256', kid: jwk.kid })).toString('base64url') + '.' +
    Buffer.from(JSON.stringify({ iss: issuer, aud: 'agenthub', sub: user, preferred_username: user,
      email: user + '@example.com', iat: now, exp: now + 3600 })).toString('base64url');
  return input + '.' + sign('sha256', Buffer.from(input), privateKey).toString('base64url');
};
// Synthetic identities exist only in the disposable quickstart cluster, with no real login.
http.createServer((req, res) => {
  res.setHeader('Content-Type', 'application/json');
  let body;
  if (req.url === '/.well-known/openid-configuration') body = { issuer, jwks_uri: issuer + '/keys',
    authorization_endpoint: issuer + '/authorize', token_endpoint: issuer + '/token',
    response_types_supported: ['code'], subject_types_supported: ['public'], id_token_signing_alg_values_supported: ['RS256'] };
  else if (req.url === '/keys') body = { keys: [jwk] };
  else if (req.url === '/proof-tokens') body = { admin: token('support-admin'), user: token('support-user') };
  else { res.statusCode = 404; body = {}; }
  res.end(JSON.stringify(body));
}).listen(new URL(issuer).port, '0.0.0.0');
