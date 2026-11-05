// #452 Chromium proof: the real web app (Vite, this branch) in real Chromium, through hub.mjs to
// the real backend conversation path and to the real Codex in the real session agent.
// Logs once a second: rows in the conversation, its last row, and the pending "sent" rows.
import { createRequire } from 'node:module'
import fs from 'node:fs'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PW || new URL('../../browser-runtime/node_modules/playwright-core', import.meta.url).pathname)
const SHOTS = process.env.SHOTS || (process.env.RIG_HOME + '/shots')
const TAG = process.env.TAG || 'run'
const executablePath = fs.readdirSync(process.env.HOME + '/.cache/ms-playwright').filter(d => d.startsWith('chromium_headless_shell'))
  .map(d => process.env.HOME + '/.cache/ms-playwright/' + d + '/chrome-headless-shell-linux64/chrome-headless-shell')[0]
const t0 = Date.now()
const log = (...a) => console.log(`[${((Date.now() - t0) / 1000).toFixed(1)}s]`, ...a)
const hub = (path) => fetch('http://127.0.0.1:8080' + path, { method: 'POST' }).then(r => r.json())

const browser = await chromium.launch({ executablePath, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } })
page.on('pageerror', e => log('pageerror', e.message))
await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
await page.goto('http://localhost:5173/s/s1')
const composer = page.locator('[data-workspace-composer] textarea')
await composer.waitFor({ timeout: 30000 })
await page.waitForFunction(() => !document.querySelector('[data-workspace-composer] textarea')?.disabled, null, { timeout: 60000 })
await page.waitForTimeout(4000)

const state = () => page.evaluate(() => {
  const rows = [...document.querySelectorAll('[data-workspace-conversation] [data-workspace-role]')]
  const pending = [...document.querySelectorAll('[data-pending-message]')].map(el => el.innerText.replace(/\s+/g, ' ').slice(0, 70))
  return { rows: rows.length, last: (rows.at(-1)?.innerText || '').replace(/\s+/g, ' ').slice(0, 60), pending }
})
let watching = true
const samples = []
;(async () => { while (watching) { const s = await state().catch(() => null); if (s) { samples.push({ t: Date.now() - t0, ...s }); log('rows=' + s.rows, 'pending=' + s.pending.length, JSON.stringify(s.last)) } await page.waitForTimeout(1000) } })()
async function send(text) { await composer.fill(text); await composer.press('Enter'); log('SENT', text) }
async function until(fn, ms, what) { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return true; await page.waitForTimeout(250) } log('TIMEOUT', what); return false }
const conversationHas = text => page.evaluate(t => [...document.querySelectorAll('[data-workspace-conversation] [data-workspace-role]')].some(el => el.innerText.includes(t)), text)

// MSG_SUFFIX='\n' sends the messages the way a composer draft ending in a new line does.
const suffix = (process.env.MSG_SUFFIX || '').replace(/\\n/g, '\n')
await send(process.env.TASK || 'Do the WORK please')
await page.waitForTimeout(1500)
await send('MSG-1 sent while the turn runs' + suffix)
await page.waitForTimeout(2500)
await send('MSG-2 also sent while the turn runs' + suffix)
await page.waitForTimeout(1500)
const scenario = process.env.SCENARIO || 'drop'
if (scenario === 'drop' || scenario === 'both') { log('DROP', JSON.stringify(await hub('/__drop'))) }
if (scenario === 'stall' || scenario === 'both') { log('STALL', JSON.stringify(await hub('/__stall?n=' + (process.env.STALL_N || 1)))) }
await page.waitForTimeout(500)
await page.screenshot({ path: `${SHOTS}/${TAG}-1-during.png` })
// After the drop: the agent keeps working, and a further message goes in once the socket is back.
await page.waitForFunction(() => !document.querySelector('[data-workspace-composer] textarea')?.disabled, null, { timeout: 60000 })
await page.waitForTimeout(3000)
await send('MSG-3 sent after the drop' + suffix)
const done = await until(() => conversationHas('MSG-3'), 60000, 'MSG-3 in the conversation')
const took3 = await until(async () => (await state()).pending.length === 0, 20000, 'no pending rows')
await page.waitForTimeout(3000)
watching = false
const final = await state()
await page.screenshot({ path: `${SHOTS}/${TAG}-2-after.png` })
log('RESULT', TAG, 'scenario=' + scenario, 'conversation reached MSG-3=' + done, 'pending cleared=' + took3,
  'final rows=' + final.rows, 'pending=' + JSON.stringify(final.pending), 'MSG-3 in conversation=' + await conversationHas('MSG-3'))
fs.writeFileSync(`${SHOTS}/${TAG}-samples.json`, JSON.stringify(samples))
await browser.close()
