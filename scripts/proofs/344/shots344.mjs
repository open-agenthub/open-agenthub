// #344: screenshots of each Claude and Codex sign-in step in Chromium, real CLIs behind
// scripts/proofs/242/browser-hub.cjs. usage: APP=http://localhost:5175 node shots344.mjs <label>
// The terminal and device code are masked; authorization values never reach a log or file.
import { createRequire } from 'node:module'
import fs from 'node:fs'
import path from 'node:path'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_CORE || path.resolve(path.dirname(new URL(import.meta.url).pathname), '../../../browser-runtime/node_modules/playwright-core'))
const label = process.argv[2]
const APP = process.env.APP || 'http://localhost:5173'
const hub = 'http://127.0.0.1:8080'
const out = path.join(process.env.SHOTS || '.', 'shots'); fs.mkdirSync(out, { recursive: true })
const executablePath = fs.readdirSync(path.join(process.env.HOME, '.cache/ms-playwright')).filter(d => d.startsWith('chromium_headless_shell'))
  .map(d => path.join(process.env.HOME, '.cache/ms-playwright', d, 'chrome-headless-shell-linux64/chrome-headless-shell'))[0]
const browser = await chromium.launch({ executablePath, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const context = await browser.newContext({ viewport: { width: 1280, height: 900 } })
await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: APP })
const page = await context.newPage()
const errors = []; page.on('pageerror', e => errors.push(e.message))
await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
const composer = page.locator('[data-workspace-composer] textarea')
const card = page.locator('[data-cli-signin]')
const result = { label, steps: [] }
async function open(agent, mock = false) {
  await fetch(hub + '/__proof/agent', { method: 'POST', body: JSON.stringify({ agent, mock }) })
  await page.goto(APP + '/s/signin-proof')
  await page.locator('[data-workspace-composer]').waitFor({ timeout: 30000 })
}
// What a person sees: every button and link in the card or menu notice, and which is primary.
async function snap(name, locator) {
  await page.mouse.move(0, 0); await page.waitForTimeout(600)
  const controls = await locator.locator('button, a').evaluateAll(els => els.filter(el => el.offsetParent).map(el => ({
    text: el.innerText.trim(), primary: el.classList.contains('primary'), tag: el.tagName.toLowerCase() })))
  const text = (await locator.innerText()).replace(/[A-Z0-9]{4,}-[A-Z0-9]{4,}/g, '[code]')
  result.steps.push({ name, controls, text })
  await locator.screenshot({ path: path.join(out, `344-${name}-${label}.png`), mask: [page.locator('[data-cli-device-code]')] })
}
const notice = () => page.locator('[data-cli-menu], [data-cli-signin]').first()

await open('claude', true)
await page.waitForTimeout(2500)
await composer.fill('/login'); await composer.press('Enter')
await page.locator('[data-cli-option="1"], [data-cli-signin-option="1"]').first().waitFor({ timeout: 15000 })
await snap('claude-1-method', notice())
await page.locator('[data-cli-option="1"], [data-cli-signin-option="1"]').first().click()
await card.locator('[data-cli-signin-code]').waitFor({ timeout: 15000 })
await snap('claude-2-code', card)
await context.route('https://**', route => route.fulfill({ status: 200, body: 'sign-in page stand-in' }))
const [tab] = await Promise.all([context.waitForEvent('page'), card.locator('[data-cli-signin-open]').click()])
result.openedInNewTab = Boolean(tab); result.opener = await tab.evaluate(() => window.opener === null ? 'none' : 'present'); await tab.close()
await snap('claude-2b-opened', card)
await page.setViewportSize({ width: 390, height: 844 }); await page.waitForTimeout(1200)
await snap('claude-2-code-mobile', card)
await page.setViewportSize({ width: 1280, height: 900 }); await page.waitForTimeout(800)
await card.locator('[data-cli-signin-code]').fill('example-authorization-code')
await card.locator('[data-cli-signin-submit]').click()
await page.getByText('Sign-in failed', { exact: true }).waitFor({ timeout: 15000 })
await snap('claude-3-failed', card)
await card.locator('[data-cli-signin-retry]').or(page.getByRole('button', { name: 'Retry sign-in', exact: true })).first().click()
await card.locator('[data-cli-signin-code]').waitFor({ timeout: 15000 })
const state = new URL(await card.locator('[data-cli-signin-open]').getAttribute('href')).searchParams.get('state')
await card.locator('[data-cli-signin-code]').fill('example-code#' + state)
await card.locator('[data-cli-signin-submit]').click()
await card.getByText(/^Signed in/).waitFor({ timeout: 15000 })
await snap('claude-4-signed-in', card)
await card.getByRole('button', { name: 'Continue', exact: true }).click()
await card.waitFor({ state: 'detached', timeout: 15000 })
result.claudeContinued = true

await open('codex', true)
await card.waitFor({ timeout: 15000 })
await card.locator('[data-cli-device-code]').waitFor({ timeout: 15000 })
await snap('codex-1-device', card)
const code = await card.locator('[data-cli-device-code]').innerText()
await card.locator('[data-cli-signin-copy]').or(card.getByRole('button', { name: /^Copy/ })).first().click()
result.codexCopied = (await page.evaluate(() => navigator.clipboard.readText())) === code
result.errors = errors
fs.writeFileSync(path.join(out, `344-${label}.json`), JSON.stringify(result, null, 1))
console.log(JSON.stringify(result, null, 1))
await browser.close()
