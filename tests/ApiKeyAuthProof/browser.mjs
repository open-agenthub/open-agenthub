// #324 browser proof: the branch's Vite app in real Chromium against the REAL backend, started
// with `node backend.mjs <dll> apiKey` (or `insecure` for the banner phase). Run:
//   CDP=http://<browser>:9222 UI=http://<this pod>:5173 PW_DIR=<dir with playwright-core> \
//     node browser.mjs <apiKey|insecure> <screenshot dir>
import { createRequire } from 'node:module'
import fs from 'node:fs'
import path from 'node:path'
import { readKey } from './backend.mjs'

const [phase = 'apiKey', out = '.'] = process.argv.slice(2)
const { chromium } = createRequire(path.join(process.env.PW_DIR || '.', 'package.json'))('playwright-core')
const UI = process.env.UI || 'http://127.0.0.1:5173'
fs.mkdirSync(out, { recursive: true })
const results = []
function check(ok, what, detail = '') {
  results.push(ok)
  console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`)
}

const browser = await chromium.connectOverCDP(process.env.CDP)
// The session browser keeps one window and closes any other, so the proof drives its existing
// tab instead of opening a context of its own.
const ctx = browser.contexts()[0]
const page = ctx.pages()[0] || await ctx.newPage()
await page.setViewportSize({ width: 1440, height: 1000 })
// Same tab, so a key from an earlier run may still be in this origin's sessionStorage.
await page.goto(UI)
await page.evaluate(() => sessionStorage.clear())
// Off the app before listening, so requests that page load started with the old key are not counted.
await page.goto('about:blank')
const statuses = []
const errors = []
page.on('pageerror', e => errors.push(String(e)))
const sockets = []
page.on('websocket', ws => {
  const entry = { url: ws.url(), frames: 0, closed: false }
  sockets.push(entry)
  ws.on('framereceived', () => { entry.frames++ })
  ws.on('close', () => { entry.closed = true })
})
page.on('response', r => { if (r.url().includes('/api/')) statuses.push([new URL(r.url()).pathname, r.status()]) })

try {
  if (phase === 'insecure') {
    await page.goto(UI)
    const banner = page.locator('[data-insecure-dev-banner]')
    await banner.waitFor({ timeout: 20000 })
    check((await banner.innerText()).includes('Insecure development mode'), 'insecure mode: the warning banner is shown')
    check(await page.locator('[data-api-key-login]').count() === 0, 'insecure mode: no key prompt')
    check(await banner.locator('button').count() === 0, 'insecure mode: the banner cannot be dismissed')
    await page.screenshot({ path: `${out}/5-insecure-banner.png` })
  } else {
    const KEY = readKey()
    await page.goto(UI)
    const prompt = page.locator('[data-api-key-login]')
    await prompt.waitFor({ timeout: 20000 })
    check(true, 'without a key the app shows the API key prompt')
    check(!statuses.some(([p, s]) => p !== '/api/config' && s === 200), 'no API data was loaded before the key', JSON.stringify(statuses))
    await page.screenshot({ path: `${out}/1-key-prompt.png` })

    await page.fill('[data-api-key-input]', 'definitely-not-the-key-0000000000000000')
    await page.click('[data-api-key-login] button[type=submit]')
    const err = page.locator('[data-api-key-error]')
    await err.waitFor()
    check((await err.innerText()).includes('not valid'), 'a wrong key is reported on the prompt', await err.innerText())
    check(await page.evaluate(() => sessionStorage.getItem('agenthub.apiKey')) === null, 'a wrong key is not stored')
    await page.screenshot({ path: `${out}/2-wrong-key.png` })

    await page.fill('[data-api-key-input]', KEY)
    await page.click('[data-api-key-login] button[type=submit]')
    await page.locator('.shell').waitFor({ timeout: 20000 })
    check(true, 'the right key opens the app')
    check(await page.evaluate(() => Object.keys(localStorage).some(k => localStorage.getItem(k)?.includes?.(sessionStorage.getItem('agenthub.apiKey')))) === false,
      'the key is in sessionStorage only, not localStorage')
    await page.locator('text=proof-324').first().waitFor({ timeout: 20000 })
    check(true, 'the key owner\'s session is listed (REST with the key)')
    check((await page.locator('.avatar').first().getAttribute('title')) === 'admin', 'the avatar names the owner "admin"')
    await page.screenshot({ path: `${out}/3-signed-in.png` })

    // Opening the session starts its WebSockets with the key in access_token.
    const sessions = await (await fetch('http://127.0.0.1:8080/api/sessions', { headers: { Authorization: `Bearer ${KEY}` } })).json()
    const id = sessions.find(x => x.title === 'proof-324')?.id
    await page.goto(`${UI}/s/${encodeURIComponent(id)}`)
    await page.waitForTimeout(5000)
    await page.screenshot({ path: `${out}/3b-session-open.png` })
    const events = sockets.find(s => s.url.includes('/events'))
    check(!!events && events.url.includes('access_token='), 'the session event socket carries the key in access_token')
    check(!!events && !events.closed, 'and stays open (the backend accepted it)', events ? `${events.frames} frames, closed=${events.closed}` : 'no socket')

    // Admin area: the key owner is an admin.
    await page.goto(`${UI}/settings/users`)
    await page.locator('[data-user-roles]').waitFor({ timeout: 20000 })
    check((await page.locator('[data-user-roles]').innerText()).includes('admin'), 'the admin area opens for the key owner')
    await page.screenshot({ path: `${out}/4-admin-area.png` })

    // A reload keeps the key (same browser session); a new context is a new browser session.
    await page.reload()
    await page.locator('.shell').waitFor({ timeout: 20000 })
    check(await page.locator('[data-api-key-login]').count() === 0, 'a reload keeps the key for the browser session')
    // A new browser session starts with empty sessionStorage; clearing it stands in for one,
    // since this browser allows a single window.
    await page.evaluate(() => sessionStorage.clear())
    await page.reload()
    await page.locator('[data-api-key-login]').waitFor({ timeout: 20000 })
    check(true, 'a new browser session (empty sessionStorage) asks for the key again')

    // A rotated key (simulated by a stale stored value) leads back to the prompt on the first 401.
    await page.evaluate(() => sessionStorage.setItem('agenthub.apiKey', 'rotated-away-0000000000000000000000000'))
    await page.goto(UI)
    await page.locator('[data-api-key-login]').waitFor({ timeout: 20000 })
    check(await page.evaluate(() => sessionStorage.getItem('agenthub.apiKey')) === null, 'a rejected stored key is dropped and the prompt returns')

    // Sign out forgets the key.
    await page.fill('[data-api-key-input]', KEY)
    await page.click('[data-api-key-login] button[type=submit]')
    await page.locator('.shell').waitFor({ timeout: 20000 })
    await page.locator('button[title="Sign out"]').click()
    await page.locator('[data-api-key-login]').waitFor({ timeout: 20000 })
    check(await page.evaluate(() => sessionStorage.getItem('agenthub.apiKey')) === null, 'sign out forgets the key')
  }
  check(errors.length === 0, 'no page errors', errors.join(' | '))
} finally {
  await page.goto('about:blank').catch(() => { })
  await browser.close()
}
const failed = results.filter(r => !r).length
console.log(`\n${results.length - failed}/${results.length} checks passed`)
process.exit(failed ? 1 : 0)
