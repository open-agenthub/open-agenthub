import { createRequire } from 'node:module'
import assert from 'node:assert/strict'
import fs from 'node:fs'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT || '/opt/session-agent/node_modules/playwright-core')
const harness = new URL('../../frontend/browser-layout-proof.html', import.meta.url)
fs.copyFileSync(new URL('./harness.html', import.meta.url), harness)
const browser = await chromium.connectOverCDP(process.env.CDP)
const context = browser.contexts()[0]
context.setDefaultTimeout(15000)
const errors = []
context.on('page', page => page.on('pageerror', error => errors.push(error.message)))
await context.route('**/api/**', route => {
  const path = new URL(route.request().url()).pathname
  const body = path.endsWith('/files') ? [] : path.endsWith('/presentation') ? { revision: 0, fileId: null } : {}
  return route.fulfill({ json: body })
})
const page = await context.newPage()
await page.setViewportSize({ width: 1440, height: 900 })
const base = process.env.UI
const shots = process.env.SHOTS
fs.mkdirSync(shots, { recursive: true })
const visible = selector => page.locator(selector).isVisible()
const width = async () => (await page.locator('[data-conversation]').boundingBox()).width
async function pop(action) {
  const pending = page.waitForEvent('popup')
  await action()
  const popup = await pending
  await popup.waitForURL(base + '/browser-popout')
  await popup.locator('[data-browser-pane]').waitFor()
  assert.equal(popup.url(), base + '/browser-popout')
  await page.getByRole('button', { name: 'Bring back', exact: true }).waitFor()
  return popup
}
try {
  await page.goto(base + '/browser-layout-proof.html')
  await page.locator('[data-browser-pane]').waitFor()
  const before = await width()
  await page.screenshot({ path: shots + '/374-before.png' })
  let popup = await pop(() => page.getByRole('button', { name: 'Pop out', exact: true }).click())
  assert.equal(await visible('.companion-side'), false)
  assert.equal(await visible('.split-seam'), false)
  assert.ok(await width() > before * 1.8)
  console.log('Conversation width:', before, '→', await width())
  await page.screenshot({ path: shots + '/374-popped.png' })
  await page.getByRole('button', { name: 'Bring back', exact: true }).click()
  await page.locator('.companion-side').waitFor()
  assert.equal(popup.isClosed(), true)
  assert.equal(await width(), before)
  popup = await pop(async () => {
    const tab = page.locator('[data-workspace-tab="browser"]')
    const box = await tab.boundingBox()
    await page.mouse.move(box.x + 15, box.y + 12)
    await page.mouse.down()
    await page.mouse.move(box.x + 80, box.y + 50, { steps: 10 })
    await page.locator('.browser-drop').waitFor()
    const target = await page.locator('.browser-drop').boundingBox()
    await page.mouse.move(target.x + target.width / 2, target.y + target.height / 2, { steps: 12 })
    await page.mouse.up()
  })
  assert.equal(await visible('.companion-side'), false)
  await popup.close()
  await page.locator('.companion-side').waitFor()
  assert.equal(await width(), before)
  await page.evaluate(() => {
    window.proofDragEnd = null
    document.addEventListener('dragend', event => {
      window.proofDragEnd = { trusted: event.isTrusted, x: event.clientX, width: innerWidth }
    }, { once: true })
  })
  popup = await pop(async () => {
    const box = await page.locator('[data-workspace-tab="browser"]').boundingBox()
    await page.mouse.move(box.x + 15, box.y + 12)
    await page.mouse.down()
    await page.mouse.move(box.x + 90, box.y + 18, { steps: 10 })
    await page.mouse.move(1480, box.y + 18, { steps: 20 })
    await page.mouse.up()
  })
  assert.equal(await visible('.companion-side'), false)
  await popup.close()
  await page.locator('.companion-side').waitFor()
  const dragEnd = await page.evaluate(() => window.proofDragEnd)
  assert.equal(dragEnd.trusted, true)
  assert.ok(dragEnd.x >= dragEnd.width)
  console.log('PASS native pointer release beyond viewport:', JSON.stringify(dragEnd))
  await page.getByRole('button', { name: 'Open Files', exact: true }).click()
  await page.locator('[data-workspace-tab="browser"]').click()
  popup = await pop(() => page.getByRole('button', { name: 'Pop out', exact: true }).click())
  assert.equal(await visible('.companion-side'), true)
  assert.equal(await visible('.files-companion'), true)
  assert.equal(await visible('[data-browser-pane]'), false)
  await page.screenshot({ path: shots + '/374-files.png' })
  await page.locator('[data-files-close]').click()
  assert.equal(await visible('.companion-side'), false)
  await popup.close()
  await page.locator('.companion-side').waitFor()
  assert.deepEqual(errors, [])
  console.log('PASS button, real pointer drag/drop, compact return, OS close, Files fallback, conversation width, credential-free child URL; no page errors')
} finally {
  await page.close()
  await browser.close()
  fs.unlinkSync(harness)
}
