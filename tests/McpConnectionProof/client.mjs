import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { pathToFileURL } from 'node:url'
import http from 'node:http'
const require = createRequire((process.env.RUNTIME || '/opt/session-agent') + '/package.json')
const sdk = name => import(pathToFileURL(require.resolve('@modelcontextprotocol/sdk/' + name)).href)
const { Client } = await sdk('client/index.js')
const { StreamableHTTPClientTransport } = await sdk('client/streamableHttp.js')
const { chromium } = require(process.env.PW_MODULE)
const base = 'https://127.0.0.1:18443'
const config = await (await fetch(base + '/api/config')).json()
assert.equal(config.mcpEnabled, true)
assert.equal(config.mcpUrl, base + '/mcp')
assert.equal(config.authMode, 'oidc')
const challenge = await fetch(config.mcpUrl, { method: 'POST', headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'proof', version: '1' } } }) })
assert.equal(challenge.status, 401)
assert(challenge.headers.get('www-authenticate').includes('/.well-known/oauth-protected-resource'))
let info, tokens, verifier, authorize
const state = 'proof-client-state'
const redirectUrl = 'http://127.0.0.1:18430/callback'
const provider = {
  redirectUrl, clientMetadata: { client_name: 'AgentHub proof', redirect_uris: [redirectUrl], grant_types: ['authorization_code', 'refresh_token'],
    response_types: ['code'], token_endpoint_auth_method: 'none', scope: 'mcp offline_access' },
  state: () => state, clientInformation: () => info, saveClientInformation: x => { info = x },
  tokens: () => tokens, saveTokens: x => { tokens = x }, saveCodeVerifier: x => { verifier = x }, codeVerifier: () => verifier,
  redirectToAuthorization: url => { authorize = url }
}
const transport = new StreamableHTTPClientTransport(new URL(config.mcpUrl), { authProvider: provider })
const first = new Client({ name: 'proof', version: '1' })
await assert.rejects(first.connect(transport))
assert(authorize, 'SDK discovers OAuth and registers client')
const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
let callback
const finished = new Promise(resolve => {
  callback = http.createServer((req, res) => { const url = new URL(req.url, redirectUrl); res.end('Client connected'); resolve(url) }).listen(18430, '127.0.0.1')
})
try {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
  const page = await ctx.newPage()
  await page.goto(authorize.href)
  await page.waitForURL('**/oidc/authorize?**')
  await page.fill('[name=username]', 'alice')
  await page.click('button')
  await page.waitForSelector('button[name="submit.Accept"]')
  assert((await page.textContent('body')).includes('alice'))
  await page.click('button[name="submit.Accept"]')
  const returned = await finished
  assert.equal(returned.searchParams.get('state'), state)
  assert(returned.searchParams.get('code'))
  await transport.finishAuth(returned.searchParams.get('code'))
  assert(tokens?.access_token)
  const payload = JSON.parse(Buffer.from(tokens.access_token.split('.')[1], 'base64url'))
  assert.equal(payload.sub, 'alice')
  assert.equal(payload.aud, config.mcpUrl)
  const client = new Client({ name: 'proof', version: '1' })
  await client.connect(new StreamableHTTPClientTransport(new URL(config.mcpUrl), { authProvider: provider }))
  const tools = await client.listTools()
  assert(tools.tools.some(t => t.name === 'session_list'))
  const listed = await client.callTool({ name: 'session_list', arguments: {} })
  assert(!listed.isError, JSON.stringify(listed))
  await client.close()
  console.log('PASS canonical config; 401 discovery; SDK dynamic registration + PKCE; Chromium OIDC login + consent; code/token exchange; alice resource audience; real MCP initialize/tools/list/session_list')
} finally { callback.close(); await browser.close(); await first.close() }
