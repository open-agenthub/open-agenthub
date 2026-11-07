import { chromium } from '../../browser-runtime/node_modules/playwright-core/index.mjs'

const browser = await chromium.connectOverCDP(process.env.PROOF_CDP_URL)
try {
  const context = browser.contexts()[0]
  const page = await context.newPage()
  await page.setViewportSize({ width: 1280, height: 900 })
  await page.goto(`${process.env.PROOF_UI_URL}/license-proof.html`)
  await page.locator('[data-report-warning]').waitFor()
  const health = await page.locator('[data-report-health]').innerText()
  if (!health.includes('never') || !health.includes('HTTP 503')) throw new Error('Actual API report health missing')
  if (await page.locator('[data-report-warning]').getAttribute('role') !== 'alert') throw new Error('Missing warning role')
  await page.locator('[data-refresh-report]').click()
  await page.locator('[data-report-warning]').waitFor()
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 })
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)
    if (overflow) throw new Error(`Admin content overflows at ${width}px`)
  }
  await page.setViewportSize({ width: 1280, height: 900 })
  if (process.env.PROOF_SCREENSHOT_PATH) await page.screenshot({ path: process.env.PROOF_SCREENSHOT_PATH, fullPage: true })
  console.log('PASS: Chromium renders real Admin API failure, signed stop date, warning and refresh at desktop/mobile widths')
} finally { await browser.close() }
