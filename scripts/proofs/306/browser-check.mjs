// #306 browser proof: the web app on :5173 in real Chromium, opened before the session's
// "container" starts, against startup-timeline.cjs --serve (real entrypoint, session agent and
// pinned Claude Code). Samples what the conversation area says every 50 ms and reports each
// change with its time since the container start, then gives a task through the composer.
//   PROOF_RUNTIME=<checkout whose agent-runtime runs> node browser-check.mjs [scenario] [shots]
import { createRequire } from 'node:module'
import { spawn } from 'node:child_process'
import path from 'node:path'
import fs from 'node:fs'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_CORE)
const [scenario = 'apikey', shots = '.'] = process.argv.slice(2)
fs.mkdirSync(shots, { recursive: true })
const here = path.dirname(new URL(import.meta.url).pathname)
const runtime = process.env.PROOF_RUNTIME ? path.join(process.env.PROOF_RUNTIME, 'scripts/proofs/306/startup-timeline.cjs') : path.join(here, 'startup-timeline.cjs')
const rig = spawn(process.execPath, [runtime, scenario, '--serve'], { stdio: ['ignore', 'pipe', 'inherit'], env: { ...process.env, PROOF_DELAY_AGENT_MS: '4000' } })
let agentStart = null
const lines = []
rig.stdout.on('data', d => { for (const l of d.toString().split('\n').filter(Boolean)) { lines.push(l); if (l.includes('agentStarted')) agentStart = Date.now() } })
while (!lines.length) await new Promise(r => setTimeout(r, 50))
const since = () => agentStart === null ? 'before start' : `${Date.now() - agentStart} ms`
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
let failed = false
const changes = []
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } })
  await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
  await page.goto('http://localhost:5173/s/s1')
  const state = () => page.evaluate(() => {
    const starting = document.querySelector('[data-conversation-starting]')
    if (starting) return 'starting: ' + starting.querySelector('p')?.textContent
    const timeline = document.querySelector('[data-workspace-conversation] .timeline')?.innerText || ''
    if (/Your agent’s workspace/.test(timeline)) return 'invitation'
    return 'conversation: ' + timeline.replace(/\s+/g, ' ').slice(0, 80)
  })
  let last = ''
  const deadline = Date.now() + 40000
  while (Date.now() < deadline) {
    const now = await state()
    if (now !== last) { changes.push(`${since()}: ${now}`); last = now }
    if (now === 'invitation' && changes.length > 1) break
    await page.waitForTimeout(50)
  }
  for (const c of changes) console.log(c)
  await page.screenshot({ path: path.join(shots, `306-${path.basename(path.dirname(path.dirname(path.dirname(runtime))))}-${scenario}.png`) })
  if (last !== 'invitation') throw new Error('still not inviting a task: ' + last)
  // The invitation is only right if the CLI takes the task: give one through the composer. Only
  // where a local model answers; a fake login would send it to the provider.
  if (scenario !== 'apikey') throw Object.assign(new Error('done'), { skip: true })
  const composer = page.locator('[data-workspace-composer] textarea')
  await composer.fill('say done')
  await composer.press('Enter')
  const until = Date.now() + 30000
  while (!lines.some(l => l.includes('modelGotTask')) && Date.now() < until) await page.waitForTimeout(100)
  if (!lines.some(l => l.includes('modelGotTask'))) throw new Error('the task never reached the model')
  console.log(`${since()}: the task typed in the composer reached the model through the CLI`)
} catch (error) {
  if (error.skip) { console.log('(no task given: no local model in this scenario)') } else {
  failed = true
  console.log('FAILED', error.message.split('\n')[0]) }
} finally {
  await browser.close()
  rig.kill('SIGTERM')
  if (rig.exitCode === null && rig.signalCode === null) await new Promise(resolve => rig.once('exit', resolve))
}
console.log(failed ? 'RESULT: failed' : 'RESULT: passed')
process.exit(failed ? 1 : 0)
