// Chromium proof of the Workspace model picker (#478). This branch's web app (Vite on :5173) talks
// to a stand-in hub on :8080 that forwards the model routes to the proof host (host/, the REAL
// SessionModelsController and SessionModelSwitch on Postgres, :8081) and the terminal socket to
// the REAL session agent (common/server.js + Codex driver) running the REAL pinned Codex CLI
// against the recording mock. Everything else the page asks for gets a canned answer.
//
// Checks: the owner's picker lists the account's models and says the switch is live; a pick is
// confirmed by the CLI and shown; the next turn reaches the new model with the first turn still in
// it; a switch during a running turn is refused with the reason and nothing is typed; a viewer
// sees the model without a control and the API refuses them; a collaborator gets the control;
// the picker at 390 px.
//
// usage: node browser-proof.mjs <out dir>
// env:   CLIS, XTERM (as for cli-switch.mjs), PLAYWRIGHT_CORE=<dir of playwright-core>,
//        CHROME=<chrome-headless-shell>, HOST=http://127.0.0.1:8081 (proof host, seeded by this script)
import http from 'node:http'
import net from 'node:net'
import fs from 'node:fs'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { MARKER, SLOW, prepareAgent, startMock } from './fixtures.mjs'

const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_CORE)
const here = path.dirname(new URL(import.meta.url).pathname)
const RUNTIME = path.resolve(process.env.PROOF_RUNTIME || path.join(here, '../../agent-runtime'))
const CLIS = path.join(process.env.CLIS || '.', 'node_modules/.bin')
const HOST = process.env.HOST_URL || 'http://127.0.0.1:8081'
const out = path.resolve(process.argv[2] || 'browser-out')
fs.rmSync(out, { recursive: true, force: true })
fs.mkdirSync(path.join(out, 'tmp'), { recursive: true })
const results = []
const check = (name, ok, detail = '') => { results.push({ name, ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`) }
const wait = ms => new Promise(resolve => setTimeout(resolve, ms))

// ---- the real session agent with the real Codex CLI ----
const log = path.join(out, 'model.jsonl')
const mock = await startMock({ log, slowMs: 12_000 })
const requests = () => fs.readFileSync(log, 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line))
const home = fs.mkdtempSync(path.join(out, 'home-'))
const work = fs.mkdtempSync(path.join(out, 'work-'))
const prepared = prepareAgent('codex', { home, work, port: mock.port, clis: CLIS })
// Fixed, not random: the seeded row keeps the token of its first insert across runs.
const token = 'proof-callback-token-0123456789'
const agentPort = Number(process.env.AGENT_PORT || 27681)
const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW|RUNTIME$)/.test(k)))
const agent = spawn(process.execPath, [path.join(RUNTIME, 'common', 'server.js')], {
  cwd: work,
  stdio: ['ignore', fs.openSync(path.join(out, 'session-agent.log'), 'w'), fs.openSync(path.join(out, 'session-agent.log'), 'a')],
  env: {
    ...base, ...prepared.env, HOME: home, TERM: 'xterm-256color', TMPDIR: path.join(out, 'tmp'), PATH: CLIS + ':' + process.env.PATH,
    AGENTHUB_DRIVER: path.join(RUNTIME, 'codex', 'driver.js'), AGENTHUB_PORT: String(agentPort), AGENTHUB_MODE: 'interactive',
    AGENTHUB_WORKDIR: work, AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:8080/internal/sessions/proof-session',
    AGENTHUB_CALLBACK_TOKEN: token, AGENTHUB_SESSION_ID: 'proof-session',
    // Never this pod's own watcher or restore marker: the session agent signals the pid it finds.
    AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(out, 'watcher.pid'), AGENTHUB_STATE_RESTORED: '0'
  }
})

// ---- the stand-in hub ----
let user = 'alice'
const roles = { alice: undefined, bob: 'Viewer', carol: 'Collaborator' }
async function sessionJson() {
  const row = await (await fetch(HOST + '/__session')).json()
  return { id: 'proof-session', title: 'Model switch proof', owner: 'alice', mode: 'Interactive', agent: 'Codex', authMode: 'ApiKey',
    phase: 'Running', status: 'Running', uiMode: 'terminal', podIp: '127.0.0.1', modelName: row.modelName, accessRole: roles[user],
    sharedBy: roles[user] ? 'alice' : undefined, browser: { phase: 'Stopped' }, effectiveSecrets: [] }
}
function forward(req, res, target) {
  const proxied = http.request(target + req.url, { method: req.method, headers: { ...req.headers, host: new URL(target).host } }, answer => {
    res.writeHead(answer.statusCode, answer.headers)
    answer.pipe(res)
  })
  proxied.on('error', () => { res.writeHead(502); res.end() })
  req.pipe(proxied)
}
const json = (res, body, status = 200) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(body)) }
const frontLog = path.join(out, 'front.log')
const front = http.createServer(async (req, res) => {
  const url = req.url.split('?')[0]
  fs.appendFileSync(frontLog, req.method + ' ' + url + '\n')
  if (url.startsWith('/api/sessions/proof-session/model')) return forward(req, res, HOST)
  if (url === '/api/sessions/proof-session') return json(res, await sessionJson())
  if (url === '/api/sessions') return json(res, roles[user] ? [] : [await sessionJson()])
  if (url === '/api/sessions/shared') return json(res, roles[user] ? [await sessionJson()] : [])
  if (url === '/api/config') return json(res, { authority: '' })
  if (url === '/api/agents/allowed') return json(res, { agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw', 'OpenCode'] })
  if (url === '/api/credentials') return json(res, { codexApiKey: true })
  if (url === '/api/credentials/accounts') return json(res, {})
  if (url.startsWith('/internal/')) { req.resume(); return json(res, url.endsWith('/messages') ? { messages: [] } : {}) }
  if (req.method === 'GET' && /\/(conversation|transcript)$/.test(url)) return json(res, { items: [], offset: 0, total: 0, source: 'scrollback' })
  if (req.method === 'GET') return json(res, [])
  req.resume()
  json(res, {})
})
// The Workspace's terminal socket, as the hub's TerminalProxy carries it: straight to the agent.
front.on('upgrade', (req, socket, head) => {
  if (!req.url.startsWith('/ws/sessions/proof-session/terminal')) return socket.destroy()
  const upstream = net.connect(agentPort, '127.0.0.1', () => {
    upstream.write(`GET / HTTP/1.1\r\n` + Object.entries(req.headers).map(([k, v]) => `${k}: ${k === 'host' ? '127.0.0.1:' + agentPort : v}`).join('\r\n') + '\r\n\r\n')
    if (head?.length) upstream.write(head)
    upstream.pipe(socket)
    socket.pipe(upstream)
  })
  upstream.on('error', () => socket.destroy())
  socket.on('error', () => upstream.destroy())
})
// Vite's proxy target is localhost:8080, which Node resolves to ::1 first; loopback only, both families.
const front6 = http.createServer((req, res) => front.emit('request', req, res))
front6.on('upgrade', (req, socket, head) => front.emit('upgrade', req, socket, head))
await new Promise(resolve => front.listen(8080, '127.0.0.1', resolve))
await new Promise(resolve => front6.listen(8080, '::1', resolve))
const as = async name => { user = name; await fetch(HOST + '/__as/' + name, { method: 'POST' }) }
await fetch(HOST + '/__seed?token=' + token, { method: 'POST' })
await as('alice')

// ---- Chromium ----
const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } })
const errors = []
page.on('pageerror', error => errors.push(error.message))
// Any failure leaves its screen and the page text behind, and the processes are stopped either way.
try {
  await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
  await page.goto('http://localhost:5173/s/proof-session')
  const composer = page.locator('[data-workspace-composer] textarea')
  await composer.waitFor({ timeout: 60_000 })
  await page.waitForFunction(() => !document.querySelector('[data-workspace-composer] textarea')?.disabled, null, { timeout: 90_000 })
  // The Codex TUI draws its prompt a few seconds after the socket is up.
  await wait(12_000)
  async function send(text) {
    await composer.fill(text)
    await composer.press('Enter')
  }
  const chip = page.locator('[data-model-switch]')
  await chip.waitFor({ timeout: 30_000 })
  check('the session bar shows the model, the CLI default before any choice', (await page.textContent('[data-model-current]')).trim() === 'CLI default')
  await chip.screenshot({ path: path.join(out, '1-chip-default.png') })

  await send(`Remember the word ${MARKER}. Reply briefly.`)
  for (let i = 0; i < 60 && !requests().some(r => r.stream); i++) await wait(500)
  await wait(4_000)
  check('first turn reached the start model', requests().filter(r => r.stream).at(-1)?.model === 'gpt-6.1-sol', requests().filter(r => r.stream).at(-1)?.model)

  // What the hub answers for the session's models, kept for the record.
  fs.writeFileSync(path.join(out, 'pod-catalog.json'), await (await fetch(`http://127.0.0.1:${agentPort}/agenthub/models`, { headers: { 'X-Agent-Token': token } })).text())
  fs.writeFileSync(path.join(out, 'catalog.json'), await (await fetch(HOST + '/api/sessions/proof-session/models')).text())
  await page.click('[data-model-switch-toggle]')
  await page.waitForSelector('[data-model-switch-option]', { timeout: 90_000 })
  const options = await page.locator('[data-model-switch-option]').evaluateAll(els => els.map(e => e.getAttribute('data-model-switch-option')))
  check('the picker lists the account\'s models from the real catalog', options.includes('gpt-6-luna') && options.length >= 3, options.join(', '))
  check('it says the switch is live, before anything is pressed', /running conversation/.test(await page.textContent('[data-model-switch-mode]')))
  await page.click('[data-model-switch-option="gpt-6-luna"]')
  await page.locator('[data-model-switch-pop]').screenshot({ path: path.join(out, '2-picker-open.png') })
  await page.click('[data-model-switch-apply]')
  await page.waitForSelector('[data-model-switch-result]', { timeout: 40_000 })
  const confirmation = await page.textContent('[data-model-switch-result]')
  check('the CLI confirmed the switch and the picker shows its line', /Now on gpt-6-luna\. The agent said: “• Model changed to gpt-6-luna/.test(confirmation), confirmation)
  check('the bar shows the new model', (await page.textContent('[data-model-current]')).trim() === 'gpt-6-luna')
  await page.screenshot({ path: path.join(out, '3-switched.png') })
  const stored = (await (await fetch(HOST + '/__session')).json()).modelName
  check('the session row holds the new model for the next restart', stored === 'gpt-6-luna', stored)
  await page.keyboard.press('Escape')

  const before = requests().length
  await send('Which word did I ask you to remember?')
  for (let i = 0; i < 60 && !requests().slice(before).some(r => r.stream); i++) await wait(500)
  await wait(3_000)
  const second = requests().slice(before).filter(r => r.stream).at(-1)
  check('the next turn reached the new model', second?.model === 'gpt-6-luna', second?.model)
  check('and still carried the first turn', second?.markerInRequest === true)

  // A switch while a turn runs.
  const beforeSlow = requests().length
  await send(`${SLOW}: take your time.`)
  for (let i = 0; i < 40 && requests().length === beforeSlow; i++) await wait(250)
  await wait(2_000)
  await page.click('[data-model-switch-toggle]')
  await page.click('[data-model-switch-option="gpt-6.1-sol"]')
  const disabledByActivity = await page.locator('[data-model-switch-apply]').isDisabled()
  if (!disabledByActivity) await page.click('[data-model-switch-apply]')
  await page.waitForSelector('[data-model-switch-busy], [data-model-switch-result]', { timeout: 20_000 })
  const refusal = await page.locator('[data-model-switch-busy], [data-model-switch-result]').first().textContent()
  check('a switch during a running turn is refused with the reason', /working/.test(refusal), refusal)
  await page.locator('[data-model-switch-pop]').screenshot({ path: path.join(out, '4-refused-while-working.png') })
  await page.keyboard.press('Escape')
  for (let i = 0; i < 60 && !requests().slice(beforeSlow).some(r => r.stream); i++) await wait(500)
  await wait(14_000)
  check('the running turn stayed on its model and nothing was switched under it',
    requests().slice(beforeSlow).filter(r => r.stream).every(r => r.model === 'gpt-6-luna') &&
    (await (await fetch(HOST + '/__session')).json()).modelName === 'gpt-6-luna')

  // A viewer, then a collaborator.
  await as('bob')
  await page.goto('http://localhost:5173/s/proof-session')
  await page.waitForSelector('[data-model-readonly]', { timeout: 30_000 })
  check('a viewer sees the model', (await page.textContent('[data-model-current]')).trim() === 'gpt-6-luna')
  check('a viewer gets no control', await page.locator('[data-model-switch-toggle]').count() === 0)
  await page.locator('[data-model-switch]').screenshot({ path: path.join(out, '5-viewer.png') })
  const viewerPost = await fetch(HOST + '/api/sessions/proof-session/model', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"model":"gpt-6-sol"}' })
  check('the API refuses a viewer\'s switch', viewerPost.status === 403, String(viewerPost.status))
  await as('carol')
  await page.goto('http://localhost:5173/s/proof-session')
  await page.waitForSelector('[data-model-switch-toggle]', { timeout: 30_000 })
  check('a collaborator gets the control', true)

  await as('alice')
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('http://localhost:5173/s/proof-session')
  await page.waitForSelector('[data-model-switch-toggle]', { timeout: 30_000 })
  await page.click('[data-model-switch-toggle]')
  await page.waitForSelector('[data-model-switch-pop]')
  const box = await page.locator('[data-model-switch-pop]').boundingBox()
  check('at 390 px the picker stays on screen', box && box.x >= 0 && box.x + box.width <= 390, JSON.stringify(box))
  await page.screenshot({ path: path.join(out, '6-mobile.png') })
  check('no page errors', errors.length === 0, errors.join(' | '))
} catch (error) {
  check('the run finished', false, error.message.split('\n')[0])
  await page.screenshot({ path: path.join(out, 'failure.png') }).catch(() => {})
  fs.writeFileSync(path.join(out, 'failure.txt'), await page.textContent('body').catch(() => ''))
}
fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify(results, null, 2))
await browser.close()
agent.kill('SIGTERM')
await wait(3_000)
try { agent.kill('SIGKILL') } catch {}
front.close()
front6.close()
mock.close()
console.log(`${results.filter(r => r.ok).length}/${results.length} passed`)
process.exit(results.every(r => r.ok) ? 0 : 1)
