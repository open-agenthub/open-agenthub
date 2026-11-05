const { chromium } = require('../../browser-runtime/node_modules/playwright-core')
const assert = require('node:assert/strict')
const fs = require('node:fs/promises')
;(async () => {
  const browser = await chromium.connectOverCDP(process.env.CDP_URL)
  const context = browser.contexts()[0]
  const page = await context.newPage()
  const errors = []
  page.on('pageerror', e => errors.push(e.message))
  const out = process.env.PROOF_OUTPUT
  await fs.mkdir(out, { recursive: true })
  for (const width of [1440, 390]) {
    for (const mode of ['personal', 'org']) {
      await page.setViewportSize({ width, height: 1000 })
      await page.goto(`${process.env.UI_URL}/tests/McpLiteralWarningProof/index.html${mode === 'org' ? '?org' : ''}`)
      await page.locator('[data-mcp-share-toggle]').click()
      const warning = page.locator('[data-mcp-literal-warning]')
      await warning.waitFor()
      assert.match(await warning.innerText(), /their agents can read/)
      assert.ok(!(await warning.innerText()).includes('demo-value'))
      await page.locator('[data-share-save]').click()
      await page.waitForFunction(() => window.proofShared)
      await page.screenshot({ path: `${out}/sharing-${mode}-${width}.png`, fullPage: true })
      await page.locator('[data-mcp-share-toggle]').click()
      await page.locator('[data-mcp-edit]').click()
      await warning.waitFor()
      const bounds = await warning.boundingBox()
      assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width, JSON.stringify(bounds))
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth))
      await page.screenshot({ path: `${out}/editor-${mode}-${width}.png`, fullPage: true })
      await page.locator('[data-mcp-save]').click()
      await page.waitForFunction(() => window.proofSaved)
      assert.equal(JSON.parse(await page.evaluate(() => window.proofSaved.configJson)).headers.Authorization, 'demo-value')
    }
  }
  assert.deepEqual(errors, [])
  console.log('PASS: sharing/editor, personal/org, 1440/390; no overflow, page errors, value echo, or save/share blocking')
  await page.close()
  await browser.close()
})().catch(e => { console.error(e); process.exit(1) })
