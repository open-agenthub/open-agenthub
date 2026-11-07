// Times the first sign-in of the admin named in ee.admins and of a second user, and records what
// each sees (Admin entry or not). Usage: node first-sign-in.mjs <outdir>
import { launch, signIn, HUB } from './browser.mjs'
const out = process.argv[2] || '.'
const browser = await launch()
for (const [user, pw] of [['alice', 'alice-demo-pw'], ['bob', 'bob-demo-pw']]) {
  const ctx = await browser.newContext({ viewport: { width: 1600, height: 900 }, ignoreHTTPSErrors: false })
  const t0 = Date.now()
  const page = await signIn(ctx, user, pw)
  await page.waitForLoadState('networkidle', { timeout: 120000 }).catch(() => {})
  await page.waitForTimeout(3000)
  const ms = Date.now() - t0
  const admin = await page.getByText('Admin', { exact: true }).count()
  const me = await page.evaluate(async () => (await fetch('/api/me', { headers: { authorization: 'Bearer ' + (sessionStorage.getItem('access_token') || '') } })).status).catch(e => String(e))
  await page.screenshot({ path: `${out}/first-sign-in-${user}.png` })
  console.log(JSON.stringify({ user, signInMs: ms, url: page.url(), adminEntries: admin, me }))
  await ctx.close()
}
await browser.close()
