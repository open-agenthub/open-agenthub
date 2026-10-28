// #343 e2e in Chromium. usage: node browser343.mjs <ask|idle> <agent> <label>
//  ask:  the CLI's question tool asks a long question; record the notice (title, header, option
//        descriptions), the activity line, answer option 2 there and check the model got it.
//  idle: a plain prompt, then 80 s at the idle prompt; sample the activity line and the hub's
//        questionPending every 5 s.
import { createRequire } from 'node:module'
import fs from 'node:fs'
import path from 'node:path'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_CORE || path.resolve(path.dirname(new URL(import.meta.url).pathname), '../../../browser-runtime/node_modules/playwright-core'))
const R = path.dirname(new URL(import.meta.url).pathname)
const [scenario, agent, label] = process.argv.slice(2)
const H = process.env.RIG_HOME || R
const results = path.join(H, 'run-' + agent, 'tool-results.log')
const executablePath = fs.readdirSync(path.join(process.env.HOME, '.cache/ms-playwright')).filter(d => d.startsWith('chromium_headless_shell'))
  .map(d => path.join(process.env.HOME, '.cache/ms-playwright', d, 'chrome-headless-shell-linux64/chrome-headless-shell'))[0]
const say = (...a) => console.log(new Date().toISOString().slice(11, 23), ...a)
const shots = path.join(H, 'shots'); fs.mkdirSync(shots, { recursive: true })
const browser = await chromium.launch({ executablePath, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage()
page.on('pageerror', e => say('pageerror', e.message))
await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
await page.goto((process.env.APP || 'http://localhost:5173') + '/s/s1')
const composer = page.locator('[data-workspace-composer] textarea')
await composer.waitFor({ timeout: 30000 })
await page.waitForFunction(() => !document.querySelector('[data-workspace-composer] textarea')?.disabled, null, { timeout: 90000 })
await page.waitForTimeout(agent === 'opencode' ? 60000 : agent === 'codex' ? 20000 : 10000)
const activity = () => page.locator('[data-workspace-activity]').innerText({ timeout: 500 }).catch(() => '(none)')
const toolResults = () => fs.existsSync(results) ? fs.readFileSync(results, 'utf8') : ''
const out = { scenario, agent, label }
const hubSession = async () => (await fetch('http://127.0.0.1:8080/rig/session')).json()

if (scenario === 'ask' || scenario === 'cancel' || scenario === 'multi') {
  if (agent === 'codex') {
    // request_user_input is a Plan-mode tool; Shift+Tab in the real TUI switches to it.
    await page.click('[data-drawer-agent]'); await page.locator('.xterm:visible').click()
    await page.keyboard.press('Shift+Tab'); await page.waitForTimeout(1500)
    await page.click('[data-drawer-agent]'); await page.waitForTimeout(500)
  }
  out.ptySize = await page.evaluate(() => null)
  await composer.fill('ASK-ME please'); await composer.press('Enter')
  const notice = page.locator('[data-cli-menu]')
  await notice.waitFor({ timeout: 90000 })
  await page.waitForTimeout(2500)
  out.head = await notice.locator('.cli-menu-head').innerText()
  out.title = await page.locator('[data-cli-title]').first().innerText().catch(() => '')
  out.header = await page.locator('[data-cli-header]').innerText({ timeout: 500 }).catch(() => '')
  out.options = await page.locator('[data-cli-option]').allInnerTexts()
  out.activity = await activity()
  await notice.screenshot({ path: path.join(shots, `343-${agent}-${label}.png`) })
  await page.screenshot({ path: path.join(shots, `343-${agent}-${label}-page.png`) })
  if (scenario === 'multi') {
    await page.locator('[data-cli-option="2"]').click(); await page.waitForTimeout(1500)
    out.ticked = await page.locator('[data-cli-option="2"]').innerText()
    out.titleAfterTick = await page.locator('[data-cli-title]').first().innerText().catch(() => '')
    out.activityAfterTick = await activity()
    await page.locator('[data-cli-step]').click()
    await page.waitForFunction(() => /Submit answers/.test(document.querySelector('[data-cli-menu]')?.innerText || ''), null, { timeout: 10000 })
    out.review = await notice.innerText()
    await notice.screenshot({ path: path.join(shots, `343-${agent}-${label}-multi-review.png`) })
    await page.locator('[data-cli-option="1"]').click()
    const end = Date.now() + 30000
    while (Date.now() < end && !toolResults()) await page.waitForTimeout(300)
    out.toolResult = toolResults().slice(0, 300)
    await page.waitForTimeout(4000)
    out.noticeAfter = await notice.count(); out.activityAfter = await activity()
    say(JSON.stringify(out, null, 1)); fs.writeFileSync(path.join(H, `result-${scenario}-${agent}-${label}.json`), JSON.stringify(out, null, 1)); await browser.close(); process.exit(0)
  }
  if (scenario === 'cancel') {
    await page.locator('[data-cli-cancel]').click()
    await page.waitForTimeout(5000)
    out.noticeAfter = await notice.count()
    out.activityAfter = await activity()
    out.screen = (await page.evaluate(() => null))
    say(JSON.stringify(out, null, 1)); fs.writeFileSync(path.join(H, `result-${scenario}-${agent}-${label}.json`), JSON.stringify(out, null, 1)); await browser.close(); process.exit(0)
  }
  await page.locator('[data-cli-option="2"]').click()
  const end = Date.now() + 30000
  while (Date.now() < end && !toolResults()) await page.waitForTimeout(300)
  out.toolResult = toolResults().slice(0, 300)
  await page.waitForTimeout(4000)
  out.noticeAfter = await notice.count()
  out.activityAfter = await activity()
} else {
  await composer.fill('hello there'); await composer.press('Enter')
  const t0 = Date.now(); out.samples = []
  while (Date.now() - t0 < Number(process.env.IDLE_S || 85) * 1000) {
    await page.waitForTimeout(5000)
    out.samples.push({ s: Math.round((Date.now() - t0) / 1000), activity: await activity(), questionPending: Boolean((await hubSession()).questionPending), notice: await page.locator('[data-cli-menu]').count() })
  }
  await page.screenshot({ path: path.join(shots, `343-idle-${agent}-${label}.png`) })
}
say(JSON.stringify(out, null, 1))
fs.writeFileSync(path.join(H, `result-${scenario}-${agent}-${label}.json`), JSON.stringify(out, null, 1))
await browser.close()
