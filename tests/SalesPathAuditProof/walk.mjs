// Drives the guest's Chromium over CDP through the prospect's first minutes: open the UI, sign in
// with the generated API key, look at Admin → License, and print what each screen says. Run with
// NODE_PATH pointing at a playwright-core install; screenshots go to $SHOTS (never committed).
//   node walk.mjs <step> ; steps: signin | admin | shot <url-path> <name>
import { chromium } from 'playwright-core'
import fs from 'node:fs'
const SHOTS = process.env.SHOTS || '.'
const KEY = fs.readFileSync(process.env.KEY_FILE, 'utf8').trim()
const browser = await chromium.connectOverCDP(process.env.CDP || 'http://127.0.0.1:19222')
const ctx = browser.contexts()[0]
const page = ctx.pages()[0] || await ctx.newPage()
const t0 = Date.now()
const log = (...a) => console.log(((Date.now() - t0) / 1000).toFixed(1) + 's', ...a)
const text = async () => (await page.locator('body').innerText()).replace(/\s+/g, ' ').slice(0, 1500)
const step = process.argv[2]
if (step === 'signin') {
  await page.goto('http://localhost:8080/', { waitUntil: 'networkidle' })
  await page.screenshot({ path: `${SHOTS}/01-first-open.png` })
  log('first screen:', await text())
  const input = page.locator('input[type=password], input[type=text]').first()
  await input.fill(KEY)
  await page.keyboard.press('Enter')
  await page.waitForTimeout(4000)
  await page.screenshot({ path: `${SHOTS}/02-after-signin.png` })
  log('after sign-in:', await text())
} else if (step === 'shot') {
  await page.goto('http://localhost:8080' + process.argv[3], { waitUntil: 'networkidle' })
  await page.waitForTimeout(2500)
  await page.screenshot({ path: `${SHOTS}/${process.argv[4]}.png`, fullPage: true })
  log(process.argv[3], await text())
} else if (step === 'click') {
  await page.getByText(process.argv[3], { exact: false }).first().click()
  await page.waitForTimeout(2500)
  await page.screenshot({ path: `${SHOTS}/${process.argv[4]}.png`, fullPage: true })
  log('after click', process.argv[3], await text())
}
await browser.close().catch(() => {})
