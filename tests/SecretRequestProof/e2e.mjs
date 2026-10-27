// #330 end to end in real Chromium: a session spawned by another one through the real in-pod
// agenthub_sessions MCP (session_create), its pod run by pod.mjs with the pinned Claude Code, and
// the owner answering the agent's secret_request in the web app the way the issue describes —
// the banner in the session, "Add it…", the pre-filled form, Save.
//
// Expected with the fix: the agent's tool call returns "provided", the agent reads the file and
// sees the value, the conversation the hub serves and the Workspace show it masked, and after a
// reload neither the session nor Settings → Secrets shows the request any more.
//
// Run (backend on :8080, Vite on :5173):
//   node e2e.mjs <chrome-headless-shell> <outdir> <scratch dir for the pod>
// env: REPO, KUBECTL, KUBECONFIG_ADMIN, CLAUDE_BIN_DIR (as for pod.mjs), PGURL
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'

const [chrome, out, podDir] = process.argv.slice(2)
const REPO = process.env.REPO
const here = createRequire(import.meta.url)
const { chromium } = here('playwright-core')
const pg = here('pg')
const sdk = createRequire(path.join(REPO, 'agent-runtime/session-agent/package.json'))
const { Client } = sdk('@modelcontextprotocol/sdk/client/index.js')
const { StdioClientTransport } = sdk('@modelcontextprotocol/sdk/client/stdio.js')
const BASE = 'http://127.0.0.1:8080', UI = 'http://localhost:5173'
fs.mkdirSync(out, { recursive: true })
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }
const sleep = ms => new Promise(r => setTimeout(r, ms))
async function call(method, p, body) {
  const r = await fetch(BASE + p, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) })
  const text = await r.text()
  return { status: r.status, body: text ? (() => { try { return JSON.parse(text) } catch { return text } })() : null }
}
const VALUE = `{"type":"service_account","private_key":"e2e-330-${Date.now().toString(36)}-7a6b5c"}`
const FILE = 'deploy-key.json'

// The parent: a session the owner created. The child is what its agent spawns.
const parent = (await call('POST', '/api/sessions', { title: 'coordinator', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' })).body
const db = new pg.Client({ connectionString: process.env.PGURL || 'postgres://postgres@127.0.0.1:54329/agenthub330' })
await db.connect()
const parentToken = (await db.query('select callback_token from sessions where id = $1', [parent.id])).rows[0].callback_token
await db.end()
const mcpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mcp-'))
fs.cpSync(path.join(REPO, 'agent-runtime/sessions'), path.join(mcpDir, 'sessions'), { recursive: true })
fs.symlinkSync(path.join(REPO, 'agent-runtime/session-agent/node_modules'), path.join(mcpDir, 'node_modules'))
const parentMcp = new Client({ name: 'coordinator', version: '1.0.0' })
await parentMcp.connect(new StdioClientTransport({ command: process.execPath, args: [path.join(mcpDir, 'sessions/server.mjs')],
  env: { ...process.env, AGENTHUB_CALLBACK_URL: `${BASE}/internal/sessions/${parent.id}`, AGENTHUB_CALLBACK_TOKEN: parentToken, AGENTHUB_SESSION_ID: parent.id } }))
const child = JSON.parse((await parentMcp.callTool({ name: 'session_create', arguments: {
  title: 'deploy worker', agent: 'Claude', authMode: 'ApiKey', autoApprove: true,
  prompt: 'NEED-SECRET: deploy to staging with the service account key' } })).content[0].text)
await parentMcp.close()
check(!!child.id && (await call('GET', `/api/sessions/${child.id}`)).body.parentSessionId === parent.id, 'the coordinator spawned the worker through session_create', child.id)
const ceiling = await (async () => { const c = new pg.Client({ connectionString: process.env.PGURL || 'postgres://postgres@127.0.0.1:54329/agenthub330' }); await c.connect(); const r = (await c.query('select secret_ceiling from sessions where id = $1', [child.id])).rows[0].secret_ceiling; await c.end(); return r })()
console.log('worker ceiling at spawn:', ceiling)

// Its pod: the real runtime with the pinned CLI, started from the pod spec the hub wrote.
const podProc = spawn(process.execPath, [path.join(REPO, 'tests/SecretRequestProof/pod.mjs'), child.id, podDir], { stdio: 'inherit', env: process.env })
const modelLog = () => { try { return fs.readFileSync(path.join(podDir, 'model.log'), 'utf8') } catch { return '' } }

let open = []
for (let i = 0; i < 120 && !open.length; i++) { await sleep(1000); open = (await call('GET', `/api/secret-requests?sessionId=${child.id}`)).body || [] }
check(open.length === 1 && open[0].name === FILE, 'the agent asked through the in-pod MCP and the request is open', JSON.stringify(open))
check(/tool_use mcp__agenthub_sessions__secret_request/.test(modelLog()), 'the real CLI called secret_request')

const browser = await chromium.launch({ executablePath: chrome, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1360, height: 900 } })
const errors = [], responses = []
page.on('pageerror', e => errors.push(String(e)))
page.on('response', async r => { if (r.url().includes('/api/')) { try { responses.push({ url: r.url(), status: r.status(), body: await r.text() }) } catch {} } })

await page.goto(`${UI}/s/${child.id}`)
await page.waitForSelector('[data-secret-request]', { timeout: 30000 })
await page.screenshot({ path: path.join(out, '1-session-banner.png') })
await page.click('[data-secret-request-add]')
await page.waitForSelector('[data-secret-answering]', { timeout: 20000 })
check(await page.inputValue('[data-secret-name]') === FILE && await page.inputValue('[data-secret-delivery]') === 'file',
  'Add it… opens the form pre-filled with the name and delivery')
// ANSWER_AFTER_MS: how long the owner takes. Past 120 s the CLI has moved the call to the background.
await sleep(Number(process.env.ANSWER_AFTER_MS || 0))
await page.fill('[data-secret-value]', VALUE)
await page.screenshot({ path: path.join(out, '2-answer-form.png') })
await page.click('[data-secret-save]')
const outcome = await Promise.race([
  page.waitForSelector('[data-secret-granted]', { timeout: 20000 }).then(() => 'granted'),
  page.waitForSelector('[data-secret-answer-error]', { timeout: 20000 }).then(() => 'error')
]).catch(() => 'nothing')
await page.screenshot({ path: path.join(out, '3-after-save.png') })
const provide = responses.find(r => r.url.includes('/provide'))
check(outcome === 'granted', 'saving grants the secret to the worker', `page shows: ${outcome}; provide answered ${provide?.status} ${provide?.body}`)

// The agent: its tool call returns, the file is there, it reads it.
for (let i = 0; i < 150 && !/final /.test(modelLog()); i++) await sleep(1000)
const ml = modelLog()
check(/saw provided in the /.test(ml), 'the agent\'s secret_request returned provided', (ml.match(/saw provided in the .*/) || ['never'])[0])
check(ml.includes(`tool_result Bash: ${VALUE}`), 'the agent read the value from ~/.secrets in its pod')
check(ml.includes(`final The deploy key file holds: ${VALUE}`), 'the agent saw the value and repeated it in its reply')

// What the hub serves of the conversation: the pod's live transcript, masked before it left.
await sleep(2000)
const conversation = await call('GET', `/api/sessions/${child.id}/conversation`)
const convText = JSON.stringify(conversation.body)
check(conversation.status === 200 && convText.includes(`[secret:${FILE}]`) && !convText.includes('e2e-330-'),
  'the conversation the hub serves shows the value masked', `status ${conversation.status}, marker ${convText.includes(`[secret:${FILE}]`)}, raw ${convText.includes('e2e-330-')}`)

// The owner reloads: the request is gone from the session and from Settings → Secrets.
await page.goto(`${UI}/s/${child.id}`)
await page.waitForLoadState('networkidle').catch(() => {})
await sleep(6000)
check(await page.locator('[data-secret-request]').count() === 0, 'after a reload the session shows no request notice')
const workspaceText = await page.evaluate(() => {
  const copy = document.body.cloneNode(true)
  copy.querySelectorAll('.xterm').forEach(n => n.remove())
  return copy.innerText
})
await page.screenshot({ path: path.join(out, '4-session-after-reload.png') })
check(!workspaceText.includes('e2e-330-'), 'the Workspace outside the live terminal never shows the value')
console.log('workspace shows the marker:', workspaceText.includes(`[secret:${FILE}]`))
await page.goto(`${UI}/settings/secrets`)
await page.waitForSelector('[data-secret-list]', { timeout: 20000 })
await sleep(1000)
// The list covers every session of the owner; what matters is this request.
check(await page.locator(`[data-secret-request-row="${open[0]?.id}"]`).count() === 0, 'after a reload Settings → Secrets no longer lists the request')
await page.screenshot({ path: path.join(out, '5-settings-after-reload.png') })
const session = (await call('GET', `/api/sessions/${child.id}`)).body
check(session.questionPending === false, 'the worker is no longer flagged as waiting for its owner')
check((session.effectiveSecrets || []).some(s => s.name === FILE && s.provenance === 'requested'), 'the worker holds the secret as requested',
  JSON.stringify(session.effectiveSecrets))
check(!responses.some(r => r.body.includes('e2e-330-')), 'no API response the browser received carried the value')
check(errors.length === 0, 'no page errors', errors.join(' | '))

await browser.close()
podProc.kill('SIGTERM')
await sleep(3000)
console.log(`${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
