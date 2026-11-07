// Attach to one of the per-user Chromiums from chromes.sh. Exported for the demo scripts;
// as a CLI it runs a snippet against that user's page, for exploring the UI while writing them:
//   node cdp.mjs <alice|bob|carol> '<async JS using page>' [screenshot.png]
import { chromium } from 'playwright-core'
export const PORTS = { alice: 9301, bob: 9302, carol: 9303 }
export const PASSWORDS = { alice: 'alice-demo-pw', bob: 'bob-demo-pw', carol: 'carol-demo-pw' }
export const HUB = 'https://hub.example.com'
// Exact origin, not a prefix: "https://hub.example.com.evil.example" also starts with HUB.
export const onHub = url => { try { return new URL(String(url)).origin === HUB } catch { return false } }
export async function attach(user) {
  const browser = await chromium.connectOverCDP(`http://127.0.0.1:${PORTS[user]}`)
  const context = browser.contexts()[0]
  const page = context.pages().find(p => onHub(p.url())) || context.pages()[0] || await context.newPage()
  await page.setViewportSize({ width: 1920, height: 1080 })
  return { browser, context, page }
}
export async function ensureSignedIn(page, user) {
  if (!onHub(page.url())) await page.goto(HUB, { waitUntil: 'domcontentloaded' })
  const signIn = page.getByRole('button', { name: 'Sign in' })
  if (await signIn.isVisible({ timeout: 5000 }).catch(() => false)) {
    await signIn.click()
    await page.waitForSelector('#username', { timeout: 120000 })
    await page.fill('#username', user)
    await page.fill('#password', PASSWORDS[user])
    await Promise.all([page.waitForURL(onHub, { timeout: 120000 }), page.click('#kc-login')])
    await page.waitForTimeout(2000)
  }
}
if (import.meta.url === `file://${process.argv[1]}`) {
  const [user, code, shot] = process.argv.slice(2)
  const { browser, page } = await attach(user)
  await ensureSignedIn(page, user)
  const AsyncFunction = Object.getPrototypeOf(async () => {}).constructor
  const result = await new AsyncFunction('page', code || 'return page.url()')(page)
  if (result !== undefined) console.log(typeof result === 'string' ? result : JSON.stringify(result, null, 1))
  if (shot) await page.screenshot({ path: shot })
  await browser.close()
}
