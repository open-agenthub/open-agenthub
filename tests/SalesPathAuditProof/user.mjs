// Opens a signed-in browser context for a Keycloak user and runs one named action.
//   node user.mjs <user> <password> <action> [args]
// actions: activate | home | share <sessionTitle> <toUser> | open <sessionTitle> | say <sessionTitle> <text>
import { chromium } from 'playwright-core'
import fs from 'node:fs'
const [user, pw, action, ...args] = process.argv.slice(2)
const SH = process.env.SHOTS
const b = await chromium.connectOverCDP('http://127.0.0.1:19222')
const ctx = await b.newContext({ viewport: { width: 1440, height: 900 } })
const p = await ctx.newPage()
p.on('response', r => { if (r.status() >= 400 && !r.url().includes('/sessions/shared')) console.log('http', r.status(), r.request().method(), r.url().replace('http://localhost:8080', '').slice(0, 120)) })
p.on('request', r => { if (r.method() !== 'GET') console.log('req', r.method(), r.url().replace('http://localhost:8080', '').slice(0, 120), (r.postData() || '').slice(0, 160)) })
p.on('websocket', ws => { console.log('ws', ws.url().slice(0, 120)); ws.on('framesent', f => { const s = String(f.payload); if (!s.includes('resize') && s.length < 400) console.log('ws-sent', s.slice(0, 200)) }) })
const t0 = Date.now(); const log = (...a) => console.log(((Date.now() - t0) / 1000).toFixed(1) + 's', ...a)
const txt = async () => (await p.locator('body').innerText()).replace(/\s+/g, ' ')
await p.goto('http://localhost:8080/', { waitUntil: 'networkidle' })
await p.getByRole('button', { name: /sign in/i }).first().click()
await p.waitForURL(/kc\.example\.test/, { timeout: 30000 })
await p.fill('#username', user); await p.fill('#password', pw); await p.click('#kc-login')
await p.waitForURL(/localhost:8080/, { timeout: 60000 }); await p.waitForTimeout(5000)
log('signed in as', user)
if (action === 'activate') {
  await p.locator('text=⚙').first().click(); await p.waitForTimeout(2000)
  await p.getByText('License', { exact: true }).first().click(); await p.waitForTimeout(2000)
  await p.locator('textarea').fill(fs.readFileSync(process.env.TOKEN_FILE, 'utf8').trim())
  await p.getByRole('button', { name: 'Activate' }).click(); await p.waitForTimeout(4000)
  await p.screenshot({ path: `${SH}/11-license-active.png`, fullPage: true })
  log((await txt()).split('License License').pop().slice(0, 700))
} else if (action === 'takeover') {
  const [title, text] = args
  await p.screenshot({ path: `${SH}/12-${user}-home.png`, fullPage: true })
  log('bob home:', (await txt()).slice(0, 600))
  await p.getByText(title).locator('visible=true').first().click({ position: { x: 4, y: 4 } }); await p.waitForTimeout(8000)
  await p.screenshot({ path: `${SH}/13-${user}-opens.png`, fullPage: true })
  log('bob in session:', (await txt()).slice(0, 900))
  const box = p.locator('textarea').last()
  await box.fill(text); await box.press('Enter'); await p.waitForTimeout(15000)
  await p.screenshot({ path: `${SH}/14-${user}-sent.png`, fullPage: true })
  log('after send:', (await txt()).slice(-900))
} else {
  await p.screenshot({ path: `${SH}/${user}-${action}.png`, fullPage: true })
  log((await txt()).slice(0, 900))
}
await b.close().catch(() => {})
