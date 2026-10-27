// #305 browser proof: the branch's web app (Vite on :5173) in real Chromium, attached through
// entrypoint-sign-in.cjs --serve to the real entrypoint, session agent and pinned Claude Code.
//   node browser-check.mjs <none|login|cancel|subscription|apikey> <screenshot dir>
// Needs PLAYWRIGHT_CORE (path to playwright-core) and CHROMIUM (headless shell binary). The
// terminal is masked in screenshots: it shows the sign-in address.
import { createRequire } from 'node:module'
import { spawn } from 'node:child_process'
import path from 'node:path'
import fs from 'node:fs'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_CORE)
const [scenario = 'none', shots = '.'] = process.argv.slice(2)
fs.mkdirSync(shots, { recursive: true })
const here = path.dirname(new URL(import.meta.url).pathname)
const t0 = Date.now()
const log = (...a) => console.log(String(Date.now() - t0).padStart(6) + 'ms', ...a)

const rig = spawn(process.execPath, [path.join(here, 'entrypoint-sign-in.cjs'), scenario, '--serve'], { stdio: ['ignore', 'pipe', 'inherit'], env: process.env })
await new Promise(resolve => rig.stdout.once('data', resolve))
log('rig up:', scenario)
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
let failed = false
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } })
  page.on('pageerror', e => log('pageerror', e.message))
  await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
  await page.goto('http://localhost:5173/s/s1')
  const card = page.locator('[data-cli-signin]')
  const mask = [page.locator('.xterm')]
  if (scenario === 'subscription' || scenario === 'apikey') {
    // No sign-in may appear at all; the composer's enabled state shows the agent is attached.
    // The CLI's own screen in the terminal drawer shows the agent is attached and drawing; a
    // hidden terminal does not render its rows.
    await page.locator('[data-drawer-agent]').click()
    await page.waitForFunction(() => /Claude Code v\d|Do you want to use this API key/.test((document.querySelector('.xterm-rows')?.textContent || '').replace(/\u00a0/g, ' ')), null, { timeout: 60000 })
    log('Claude Code drew its screen')
    await page.waitForTimeout(20000)
    log('sign-in card shown =', await card.count())
    if (await card.count()) failed = true
    await page.screenshot({ path: path.join(shots, `305-${scenario}.png`), mask })
  } else {
    await card.waitFor({ timeout: 60000 })
    log('sign-in card appeared without typing:', (await card.innerText()).replace(/\s*\n\s*/g, ' | '))
    const buttons = await card.locator('button').allInnerTexts()
    log('card buttons:', JSON.stringify(buttons))
    await page.screenshot({ path: path.join(shots, `305-${scenario}-card.png`), mask })
    if (scenario === 'cancel') {
      await card.getByRole('button', { name: 'Cancel (Ctrl+C)' }).click()
      await card.waitFor({ state: 'detached', timeout: 30000 })
      log('card gone after Cancel (Ctrl+C)')
      await page.waitForTimeout(3000)
      log('card shown after the TUI started =', await card.count())
      await page.screenshot({ path: path.join(shots, '305-cancel-after.png'), mask })
    }
    if (scenario === 'login') {
      // A fake identity (scripts/proofs/242/mock-auth.cjs) accepts any code carrying the state.
      const href = await card.locator('[data-cli-signin-open]').getAttribute('href')
      const state = new URL(href).searchParams.get('state')
      await card.locator('[data-cli-signin-code]').fill('example-code#' + state)
      await card.locator('[data-cli-signin-submit]').click()
      await page.waitForFunction(() => !document.querySelector('[data-cli-signin]') || /Signed in/.test(document.querySelector('[data-cli-signin]').innerText), null, { timeout: 30000 })
      log('after the code: card =', await card.count() ? (await card.innerText()).split('\n')[0] : '(gone)')
      await card.waitFor({ state: 'detached', timeout: 30000 })
      log('card gone once the TUI prompt is drawn')
      await page.screenshot({ path: path.join(shots, '305-login-after.png'), mask })
    }
  }
} catch (error) {
  failed = true
  log('FAILED', error.message.split('\n')[0])
  const page = browser.contexts()[0]?.pages()[0]
  if (page) {
    log('page text:', (await page.locator('body').innerText().catch(() => '')).replace(/\s+/g, ' ').slice(0, 600))
    await page.screenshot({ path: path.join(shots, `305-${scenario}-failure.png`), mask: [page.locator('.xterm')] }).catch(() => {})
  }
} finally {
  await browser.close()
  rig.kill('SIGTERM')
  if (rig.exitCode === null && rig.signalCode === null) await new Promise(resolve => rig.once('exit', resolve))
}
log(failed ? 'RESULT: failed' : 'RESULT: passed')
process.exit(failed ? 1 : 0)
