// Workspace reconnect after a slow pod start (#520). The real backend (dev auth) on a real
// kube-apiserver; this script plays the kubelet and a session agent that becomes ready late.
// Chromium keeps the session's Workspace open the whole time and records its connection status.
//
// Timeline (seconds after create, scaled by PACE): the pod is held Pending, then marked Running
// with an IP whose agent is not listening yet (the container is up but the agent is still
// starting), and only later does an agent answer on that IP. The Workspace must reach
// "connected" on its own, without a reload, within RECONNECT_BUDGET_MS of the agent answering
// (default 30 s: the client gives up on a held handshake after 10 s, and a round-robin ingress
// can hand the next attempt to the held replica again).
//
// Env: UI (Vite, default http://localhost:5173), API (backend, default http://127.0.0.1:8080),
// KUBECONFIG (admin), KUBECTL, CHROME, PW_MODULE, OUT, AGENT_PORT (the backend's
// AgentHub__AgentPort; never 7681, which is this pod's own agent), STALL=connect|accept-only|apiserver.
// STALL=accept-only: while "starting", the agent IP accepts TCP but never answers the WebSocket
// handshake — what a container that has bound its port but is starved of CPU looks like.
import { createRequire } from 'node:module'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import net from 'node:net'
const require = createRequire(import.meta.url)
const { WebSocketServer } = require('ws')
const { chromium } = require(process.env.PW_MODULE || 'playwright-core')

const UI = process.env.UI || 'http://localhost:5173'
const API = process.env.API || 'http://127.0.0.1:8080'
const OUT = process.env.OUT || '.'
const PORT = Number(process.env.AGENT_PORT || 7699)
const IP = process.env.POD_IP || '127.0.0.5'
const PACE = Number(process.env.PACE || 1)
const STALL = process.env.STALL || 'connect'
const PENDING_S = 10 * PACE, STARTING_S = 40 * PACE
const RECONNECT_BUDGET_MS = Number(process.env.RECONNECT_BUDGET_MS || 30000)
const kubectl = (...args) => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', process.env.KUBECONFIG, '-n', 'agenthub-sessions', ...args], { encoding: 'utf8' })
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))
const results = []
const check = (name, ok, detail = '') => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`) }
const t0 = Date.now()
const at = () => ((Date.now() - t0) / 1000).toFixed(1) + 's'
const log = (...args) => console.log(`[${at()}]`, ...args)

async function api(method, path, body) {
  const response = await fetch(API + '/api' + path, { method, headers: { 'content-type': 'application/json' }, body: body && JSON.stringify(body) })
  return { status: response.status, body: await response.json().catch(() => null) }
}

// What a container that is up but whose agent is not yet serving looks like on the wire.
let stallServer
function startStall() {
  if (STALL !== 'accept-only') return
  const held = []
  stallServer = net.createServer(socket => { held.push(socket); socket.on('error', () => {}) })
  stallServer.listen(PORT, IP)
  stallServer.held = held
}
async function stopStall() {
  if (!stallServer) return
  for (const socket of stallServer.held) socket.destroy()
  await new Promise(resolve => stallServer.close(resolve))
}
// The ready agent: speaks the terminal subprotocol and greets every attach, like the PTY's
// screen redraw does, so the browser has output to show.
let agentConnections = 0
function startAgent() {
  const wss = new WebSocketServer({ host: IP, port: PORT, handleProtocols: () => 'tty' })
  wss.on('connection', socket => {
    agentConnections += 1
    log(`agent: attach #${agentConnections}`)
    socket.send('stand-in agent ready\r\n> ')
    socket.on('message', () => {})
  })
  return wss
}

;(async () => {
  const created = await api('POST', '/sessions', { title: '#520 slow start', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' })
  check('the session is created', created.status < 300, String(created.status))
  const id = created.body.id
  const podName = `session-${id}`
  log('session', id, 'phase', created.body.phase)

  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 } })
  const statuses = []
  await page.exposeFunction('reportStatus', (text, frames) => statuses.push({ t: Date.now() - t0, text, frames }))
  // Every WebSocket the page opens, and how it ends: the reconnect loop made visible.
  const sockets = []
  page.on('websocket', ws => {
    if (!ws.url().includes('/terminal')) return
    const entry = { opened: Date.now() - t0, closed: null }
    sockets.push(entry)
    ws.on('close', () => { entry.closed = Date.now() - t0 })
  })
  // Constructions as the app makes them. Chrome lets one WebSocket per host:port be in CONNECTING
  // and queues the rest, so a socket the app created can stay invisible to the network log.
  const constructed = []
  await page.exposeFunction('reportSocket', (url, state) => constructed.push({ t: Date.now() - t0, url: url.split('?')[0].replace(/^.*\/ws\//, ''), state }))
  await page.addInitScript(() => {
    const Native = window.WebSocket
    window.WebSocket = class extends Native {
      constructor(...args) {
        super(...args)
        window.reportSocket(String(args[0]), 'new')
        this.addEventListener('open', () => window.reportSocket(String(args[0]), 'open'))
        this.addEventListener('close', () => window.reportSocket(String(args[0]), 'close'))
      }
    }
  })
  await page.goto(`${UI}/s/${id}`)
  await page.waitForSelector('[data-open-workspace]')
  await page.click('[data-open-workspace]')
  await page.evaluate(() => {
    const read = () => document.querySelector('.term-wrap .status')?.textContent?.trim() || ''
    let last = null
    setInterval(() => { const now = read(); if (now !== last) { last = now; window.reportStatus(now) } }, 100)
  })

  // Pending: no pod IP yet.
  for (let i = 0; i < 100 && !kubectl('get', 'pod', podName, '-o', 'name', '--ignore-not-found').trim(); i++) await sleep(200)
  log('pod exists; holding it Pending for', PENDING_S, 's')
  await sleep(PENDING_S * 1000)

  // Running, agent not serving yet.
  startStall()
  kubectl('patch', 'pod', podName, '--subresource=status', '--type=merge', '-p', JSON.stringify({ status: {
    phase: 'Running', podIP: IP, podIPs: [{ ip: IP }],
    conditions: [{ type: 'Ready', status: 'False' }, { type: 'ContainersReady', status: 'False' }] } }))
  log(`pod Running at ${IP}; agent still starting (${STALL}) for`, STARTING_S, 's')
  await sleep(STARTING_S * 1000)

  // STALL=apiserver: the Kubernetes API stalls (SIGSTOP of the rig's kube-apiserver) for
  // FREEZE_S around the moment the agent becomes ready, as on an overloaded node. Every terminal
  // upgrade then waits on the backend's pod read; the clock below starts at the thaw.
  const freeze = STALL === 'apiserver' ? Number(process.env.FREEZE_S || 45) * 1000 : 0
  if (freeze) { process.kill(Number(process.env.APISERVER_PID), 'SIGSTOP'); log('kube-apiserver frozen') }
  // Ready.
  await stopStall()
  const agent = startAgent()
  if (freeze) {
    await sleep(freeze)
    process.kill(Number(process.env.APISERVER_PID), 'SIGCONT')
    log('kube-apiserver thawed')
  }
  kubectl('patch', 'pod', podName, '--subresource=status', '--type=merge', '-p', JSON.stringify({ status: {
    conditions: [{ type: 'Ready', status: 'True' }, { type: 'ContainersReady', status: 'True' }] } }))
  const readyAt = Date.now() - t0
  log('agent listening; pod Ready')
  const frozenSockets = freeze ? constructed.filter(c => c.url.endsWith('/terminal') && c.state === 'new' && c.t > readyAt - freeze).length : 0
  if (freeze) log('terminal sockets the app created while the API server was frozen:', frozenSockets)

  let connectedAt = null
  const deadline = Date.now() + RECONNECT_BUDGET_MS + 30000
  while (Date.now() < deadline) {
    const status = statuses.at(-1)?.text
    if (status === 'connected' && agentConnections > 0) { connectedAt = Date.now() - t0; break }
    await sleep(250)
  }
  await page.screenshot({ path: `${OUT}/${STALL}-workspace.png` })
  const shown = await page.locator('.term-wrap').innerText().catch(() => '')

  log('status timeline', JSON.stringify(statuses.map(s => [Math.round(s.t / 100) / 10, s.text])))
  log('terminal sockets', JSON.stringify(sockets.map(s => [s.opened / 1000, s.closed && s.closed / 1000])))
  const terminalNew = constructed.filter(c => c.url.endsWith('/terminal') && c.state === 'new')
  log('terminal sockets the app created', terminalNew.length, 'after ready:', terminalNew.filter(c => c.t > readyAt).length)
  log('other sockets', JSON.stringify(constructed.filter(c => !c.url.endsWith('/terminal')).map(c => [Math.round(c.t / 100) / 10, c.url, c.state])))
  const open = sockets.filter(s => s.closed === null)
  log('sockets still open at the end', JSON.stringify(open.map(s => s.opened / 1000)))
  check('the Workspace reconnects on its own after the agent becomes ready',
    connectedAt !== null && connectedAt - readyAt <= RECONNECT_BUDGET_MS,
    connectedAt === null ? `still "${statuses.at(-1)?.text}" ${((Date.now() - t0 - readyAt) / 1000).toFixed(0)} s after ready, agent attaches: ${agentConnections}`
      : `${((connectedAt - readyAt) / 1000).toFixed(1)} s after ready`)
  check('the agent\'s output reaches the page without a reload', shown.includes('stand-in agent ready') || agentConnections > 0, `attaches: ${agentConnections}`)

  fs.writeFileSync(`${OUT}/${STALL}-results.json`, JSON.stringify({ results, statuses, sockets, constructed }, null, 2))
  await browser.close()
  agent.close()
  await api('DELETE', `/sessions/${id}`)
  const failed = results.filter(r => !r.ok).length
  console.log(`${results.length - failed}/${results.length} passed`)
  process.exit(failed ? 1 : 0)
})().catch(error => { console.error(error); process.exit(2) })
