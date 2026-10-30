// #361 e2e in Chromium: the sidebar marker and the session status for an open question dialog.
// usage: node browser361.mjs <claude|codex> <label>
//  1. idle: a plain prompt, then IDLE_S seconds at the idle prompt (Claude Code's idle_prompt
//     notification comes about 60 s after the turn): no question marker, status not "Question open".
//  2. ask: the CLI's own question dialog (AskUserQuestion / request_user_input). Times how long the
//     sidebar marker and the header status take after the dialog is on screen; reads the tooltip.
//  3. answer: option 2 in the Workspace notice; times how long the marker takes to go.
// The hub's questionPending is sampled alongside: it keeps its #343 meaning and is not what drives
// the marker. Screenshots of the sidebar row and the header go to $RIG_HOME/shots.
import { createRequire } from 'node:module'
import fs from 'node:fs'
import path from 'node:path'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_CORE || path.resolve(path.dirname(new URL(import.meta.url).pathname), '../../browser-runtime/node_modules/playwright-core'))
const [agent, label] = process.argv.slice(2)
const H = process.env.RIG_HOME
const results = path.join(H, 'run-' + agent, 'tool-results.log')
const executablePath = fs.readdirSync(path.join(process.env.HOME, '.cache/ms-playwright')).filter(d => d.startsWith('chromium_headless_shell'))
  .map(d => path.join(process.env.HOME, '.cache/ms-playwright', d, 'chrome-headless-shell-linux64/chrome-headless-shell'))[0]
const say = (...a) => console.log(new Date().toISOString().slice(11, 23), ...a)
const shots = path.join(H, 'shots'); fs.mkdirSync(shots, { recursive: true })
const browser = await chromium.launch({ executablePath, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await (await browser.newContext({ viewport: { width: 1280, height: 860 } })).newPage()
page.on('pageerror', e => say('pageerror', e.message))
await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
await page.goto((process.env.APP || 'http://localhost:5173') + '/s/s1')
const composer = page.locator('[data-workspace-composer] textarea')
await composer.waitFor({ timeout: 30000 })
await page.waitForFunction(() => !document.querySelector('[data-workspace-composer] textarea')?.disabled, null, { timeout: 90000 })
await page.waitForTimeout(agent === 'codex' ? 20000 : 10000)

const hub = async () => (await fetch('http://127.0.0.1:8080/rig/session')).json()
const row = page.locator('[data-session-row="s1"]')
const ui = async () => ({
  marker: await row.locator('[data-session-question]').count(),
  tooltip: await row.locator('[data-session-question]').getAttribute('title', { timeout: 300 }).catch(() => null),
  sidebarStatus: await row.locator('.st').innerText({ timeout: 500 }).catch(() => ''),
  headerStatus: await page.locator('[data-session-status]').innerText({ timeout: 500 }).catch(() => ''),
  headerTooltip: await page.locator('[data-session-status]').getAttribute('title', { timeout: 300 }).catch(() => null),
  blinkingDot: await row.locator('.dot.ask').count()
})
const sample = async () => { const h = await hub(); return { ...(await ui()), hubQuestionOpen: Boolean(h.questionOpen), hubQuestionPending: Boolean(h.questionPending) } }
// Headless Chromium draws no native tooltip; the rig prints the title attribute beside the marker
// so the screenshot shows what a hover shows.
const showTooltip = () => page.evaluate(() => {
  document.querySelectorAll('[data-rig-tooltip]').forEach(n => n.remove())
  for (const el of document.querySelectorAll('[data-session-question], [data-session-status][title]')) {
    const tip = document.createElement('div'); tip.dataset.rigTooltip = ''
    tip.textContent = el.getAttribute('title')
    const r = el.getBoundingClientRect()
    Object.assign(tip.style, { position: 'fixed', left: r.left + 'px', top: (r.bottom + 4) + 'px', maxWidth: '360px', zIndex: 9999,
      background: '#fffbe6', color: '#222', border: '1px solid #999', borderRadius: '4px', padding: '3px 6px', font: '12px sans-serif' })
    document.body.append(tip)
  }
})
const shoot = async name => {
  await page.locator('.side-sessions').screenshot({ path: path.join(shots, `361-${agent}-${label}-${name}-sidebar.png`) })
  await page.locator('.term-bar').screenshot({ path: path.join(shots, `361-${agent}-${label}-${name}-status.png`) })
  await page.screenshot({ path: path.join(shots, `361-${agent}-${label}-${name}-page.png`) })
}
const waitFor = async (check, ms) => {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) { if (await check()) return Date.now() - t0; await page.waitForTimeout(200) }
  return null
}
const out = { agent, label }

// 1. idle prompt
await composer.fill('hello there'); await composer.press('Enter')
const idleSeconds = Number(process.env.IDLE_S || (agent === 'claude' ? 75 : 20))
out.idle = []
for (let t = 0; t < idleSeconds; t += 5) { await page.waitForTimeout(5000); out.idle.push({ s: t + 5, ...(await sample()) }) }
await shoot('idle')

// 2. question dialog
if (agent === 'codex') {
  // request_user_input is a Plan-mode tool; Shift+Tab in the real TUI switches to it.
  await page.click('[data-drawer-agent]'); await page.locator('.xterm:visible').click()
  await page.keyboard.press('Shift+Tab'); await page.waitForTimeout(1500)
  await page.click('[data-drawer-agent]'); await page.waitForTimeout(500)
}
await composer.fill('ASK-ME please'); await composer.press('Enter')
const notice = page.locator('[data-cli-menu]')
await notice.waitFor({ timeout: 90000 })
const noticeAt = Date.now()
out.markerAfterDialogMs = await waitFor(async () => (await row.locator('[data-session-question]').count()) > 0, 20000)
out.statusAfterDialogMs = Date.now() - noticeAt
await page.waitForFunction(() => document.querySelector('[data-session-status]')?.innerText === 'Question open', null, { timeout: 15000 }).catch(() => {})
out.open = await sample()
out.noticeTitle = await page.locator('[data-cli-title]').first().innerText().catch(() => '')
await shoot('open')
await showTooltip(); await shoot('open-tooltip')

// 3. answer
await page.evaluate(() => document.querySelectorAll('[data-rig-tooltip]').forEach(n => n.remove()))
await page.locator('[data-cli-option="2"]').click()
const answeredAt = Date.now()
out.markerGoneMs = await waitFor(async () => (await row.locator('[data-session-question]').count()) === 0, 20000)
const end = Date.now() + 30000
while (Date.now() < end && !(fs.existsSync(results) && fs.readFileSync(results, 'utf8'))) await page.waitForTimeout(300)
out.toolResult = fs.existsSync(results) ? fs.readFileSync(results, 'utf8').slice(0, 300) : ''
await page.waitForTimeout(6000)
out.answered = await sample()
out.answeredAfterMs = Date.now() - answeredAt
await shoot('answered')
const hubLog = path.join(H, 'hub.log')
out.questionStateCalls = fs.existsSync(hubLog) ? fs.readFileSync(hubLog, 'utf8').split('\n').filter(l => l.includes('question-state')) : []
say(JSON.stringify(out, null, 1))
fs.writeFileSync(path.join(H, `result-361-${agent}-${label}.json`), JSON.stringify(out, null, 1))
await browser.close()
