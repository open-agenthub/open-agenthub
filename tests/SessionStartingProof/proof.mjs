// #241 end to end: a just-created session in the branch's web app (Vite on 127.0.0.1:5173) against
// the real backend, kube-apiserver, Postgres and S3 of ../SessionResourcesProof/README.md. This
// script stands in for the kubelet and the session agent: it holds each pod Pending, marks it
// Running, and then uploads the first transcript the way common/server.js does (curl -T to the
// pod's presigned AGENTHUB_TRANSCRIPT_PUT_URL, then PUT <callback>/transcript).
// Start the backend with AgentHub__AgentPort=7699 (an unused port): every 127.x address is this
// pod's own loopback, and on the default 7681 the hub would attach to the session agent running
// this proof, and type terminal replies into it. With nothing listening, the hub accepts the
// browser's terminal socket and fails to reach the pod — the "agent is starting" stage.
// PG_CTL and PGDATA let the last step stop and restart Postgres for a real load failure.
// Run: node proof.mjs <chrome-headless-shell> <outdir> <admin-kubeconfig> [api-only]
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import { createRequire } from 'node:module'

const [exe, out, adminKubeconfig, mode] = process.argv.slice(2)
const HUB = 'http://127.0.0.1:8080/api', APP = 'http://127.0.0.1:5173'
fs.mkdirSync(out, { recursive: true })
const kubectl = (...args) => execFileSync(process.env.KUBECTL || 'kubectl',
  ['--kubeconfig', adminKubeconfig, '-n', 'agenthub-sessions', ...args], { encoding: 'utf8' })
const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }
async function api(method, p, body) {
  const res = await fetch(HUB + p, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) })
  const text = await res.text()
  return { status: res.status, body: text ? JSON.parse(text) : null }
}
async function pod(id) {
  for (let i = 0; i < 60; i++) {
    try { return JSON.parse(kubectl('get', 'pod', `session-${id}`, '-o', 'json')) } catch { await sleep(500) }
  }
  throw new Error('no pod for ' + id)
}
const agentEnv = p => Object.fromEntries((p.spec.containers.find(c => c.name === 'agent').env || []).map(e => [e.name, e.value]))
const markRunning = p => kubectl('patch', 'pod', p.metadata.name, '--subresource=status', '--type=merge', '-p',
  JSON.stringify({ status: { phase: 'Running', podIP: '127.0.0.9', podIPs: [{ ip: '127.0.0.9' }] } }))

// The first line each CLI writes to its own session file, in the shape NativeTranscript parses.
const firstLine = {
  Claude: { type: 'assistant', timestamp: new Date().toISOString(),
    message: { role: 'assistant', content: [{ type: 'text', text: 'Claude is ready.' }] } },
  Codex: { type: 'response_item', timestamp: new Date().toISOString(),
    payload: { type: 'message', role: 'assistant', content: [{ type: 'output_text', text: 'Codex is ready.' }] } }
}
async function uploadFirstTranscript(agent, env) {
  const file = `${out}/${agent}-transcript.jsonl`
  const body = JSON.stringify(firstLine[agent]) + '\n'
  fs.writeFileSync(file, body)
  if (env.AGENTHUB_TRANSCRIPT_PUT_URL) execFileSync('curl', ['-fsS', '--max-time', '30', '-T', file, env.AGENTHUB_TRANSCRIPT_PUT_URL])
  const res = await fetch(env.AGENTHUB_CALLBACK_URL + '/transcript', { method: 'PUT',
    headers: { 'Content-Type': 'application/x-ndjson', 'X-Agent-Token': env.AGENTHUB_CALLBACK_TOKEN }, body })
  return res.status
}

// Nothing but the API: what a just-created session's conversation answers, and that the
// endpoints still refuse what they refused before.
const created = {}
for (const agent of ['Claude', 'Codex']) {
  const session = (await api('POST', '/sessions', { title: `#241 ${agent} starting`, mode: 'Interactive', agent, authMode: 'ApiKey' })).body
  created[agent] = { session, pod: await pod(session.id) }
  const page = await api('GET', `/sessions/${session.id}/conversation?offset=-1`)
  check(page.status === 200 && page.body.entries.length === 0 && page.body.text === '',
    `${agent}: a just-created session's conversation is an empty page, not a 404`, `HTTP ${page.status} ${JSON.stringify(page.body)?.slice(0, 160)}`)
}
check((await api('GET', '/sessions/does-not-exist/conversation?offset=-1')).status === 404, 'an unknown session is still 404')
check((await fetch(HUB + '/shared/not-a-real-token/conversation?offset=-1')).status === 404, 'an invalid share token is still 404')
if (mode === 'api-only') {
  for (const { session } of Object.values(created)) await api('DELETE', `/sessions/${session.id}`)
  console.log(`${results.filter(Boolean).length}/${results.length} passed`)
  process.exit(results.every(Boolean) ? 0 : 1)
}

const { chromium } = createRequire(import.meta.url)('playwright-core')
const browser = await chromium.launch({ executablePath: exe, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1280, height: 860 } })
const errors = []
page.on('pageerror', e => errors.push(String(e)))
const timeline = '[data-workspace-conversation]'
const alertShown = () => page.locator(`${timeline} [role=alert]`).count().then(n => n > 0)
async function waitText(selector, text, ms = 20000) {
  for (let t = 0; t < ms; t += 250) {
    if ((await page.locator(selector).count()) && (await page.textContent(selector)).includes(text)) return true
    await sleep(250)
  }
  return false
}

for (const agent of ['Claude', 'Codex']) {
  const { session, pod: p } = created[agent]
  await page.goto(`${APP}/s/${session.id}`)
  check(await waitText('[data-conversation-starting]', 'Waiting for the session’s pod to start…'),
    `${agent}: a pending pod shows "Session starting" with its stage`)
  const ring = await page.$eval('[data-conversation-starting] .starting-mark', el => getComputedStyle(el).animationName)
  // Scoped styles suffix the keyframes name with the component's hash.
  check(ring.startsWith('starting-turn'), `${agent}: the starting state is animated`, ring)
  check(!(await alertShown()), `${agent}: no load error while the pod is pending`)
  await page.locator(timeline).screenshot({ path: `${out}/${agent}-1-pending.png` })

  markRunning(p)
  // There is no agent behind the pod IP: the hub accepts the terminal socket and then fails to reach
// the pod, so the socket opens and closes in a loop — the "agent starts" stage.
  check(await waitText('[data-conversation-starting]', 'Starting the agent…'), `${agent}: a running pod whose agent is not up yet shows "Starting the agent…"`)
  check(!(await alertShown()), `${agent}: still no load error`)
  await page.locator(timeline).screenshot({ path: `${out}/${agent}-2-agent-starting.png` })

  const status = await uploadFirstTranscript(agent, agentEnv(p))
  const ready = `${agent} is ready.`
  check(await waitText(timeline, ready), `${agent}: the first transcript entry replaces the starting state`, `callback PUT ${status}`)
  check((await page.locator('[data-conversation-starting]').count()) === 0, `${agent}: the starting state is gone`)
  await page.locator(timeline).screenshot({ path: `${out}/${agent}-3-first-entry.png` })
}

// A real load failure where there is something to load: a paused session with a saved
// transcript, opened from the session list while Postgres is down; Retry loads it once it is back.
const stopped = (await api('POST', '/sessions', { title: '#241 paused', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' })).body
const stoppedPod = await pod(stopped.id)
markRunning(stoppedPod)
await uploadFirstTranscript('Claude', agentEnv(stoppedPod))
check((await api('POST', `/sessions/${stopped.id}/pause`)).status < 300, 'the session pauses')
await page.goto(APP + '/')
await page.waitForSelector('.list li:has-text("#241 paused")', { timeout: 20000 })
const pg = (...args) => execFileSync(process.env.PG_CTL, ['-D', process.env.PGDATA, ...args], { encoding: 'utf8' })
pg('stop', '-m', 'fast')
await page.click('.list li:has-text("#241 paused")')
check(await waitText(`${timeline} [role=alert]`, 'Could not load the conversation.'), 'a real load failure of a stopped session still shows the error')
check((await page.locator('[data-conversation-starting]').count()) === 0, 'a stopped session never shows the starting state')
await page.locator(timeline).screenshot({ path: `${out}/4-paused-load-error.png` })
pg('start', '-w', '-l', process.env.PGDATA + '/../log', '-o', "-p 54329 -c listen_addresses=127.0.0.1 -c unix_socket_directories='' -c max_connections=1000")
await sleep(2000)
// Pooled connections the restart killed fail once each (57P01) before the pool replaces them,
// so a person may need more than one Retry; the error stays an error until then.
let clicks = 0, loaded = false
for (; clicks < 5 && !loaded; clicks++) {
  await page.click(`${timeline} [role=alert] button`)
  loaded = await waitText(timeline, 'Claude is ready.', 4000)
}
check(loaded, 'Retry loads it once the database is back', `after ${clicks} click(s)`)
await page.locator(timeline).screenshot({ path: `${out}/5-paused-after-retry.png` })

check(errors.length === 0, 'no page errors', errors.join(' | '))
await browser.close()
for (const id of [created.Claude.session.id, created.Codex.session.id, stopped.id]) await api('DELETE', `/sessions/${id}`).catch(() => {})
console.log(`${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
