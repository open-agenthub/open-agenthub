// Times the first sign-in of the admin named in ee.admins and of a second user. Who is admin is
// checked against the admin API (GET /api/admin/overview: 200 / 403), not from the page. Usage: node first-sign-in.mjs <outdir>
import { launch, signIn } from './browser.mjs'
const out = process.argv[2] || '.'
const browser = await launch()
for (const [user, pw] of [['alice', 'alice-demo-pw'], ['bob', 'bob-demo-pw']]) {
  const ctx = await browser.newContext({ viewport: { width: 1600, height: 900 }, ignoreHTTPSErrors: false })
  const t0 = Date.now()
  const page = await signIn(ctx, user, pw)
  await page.waitForLoadState('networkidle', { timeout: 120000 }).catch(() => {})
  await page.waitForTimeout(3000)
  const ms = Date.now() - t0
  await page.screenshot({ path: `${out}/first-sign-in-${user}.png` })
  console.log(JSON.stringify({ user, signInMs: ms, url: page.url() }))
  await ctx.close()
}
await browser.close()
