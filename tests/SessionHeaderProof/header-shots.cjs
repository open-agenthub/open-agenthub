// Screenshots and layout measurements of the Workspace session header (#551) at three widths,
// for an owner, a viewer and a shared-link reader. Run against the fixture in this directory and
// a dev frontend; see README.md.
const path = require('node:path')
const fs = require('node:fs')
const repo = process.env.AGENTHUB_REPO || path.resolve(__dirname, '../..')
const { chromium } = require(path.join(repo, 'agent-runtime/session-agent/node_modules/playwright-core'))

const appUrl = process.env.AGENTHUB_UI_URL || 'http://localhost:5173'
const shots = process.env.SHOTS || path.join(process.cwd(), 'header-shots')
const label = process.env.LABEL || 'after'
const widths = [1280, 1024, 768]
const views = [
  { name: 'owner', url: '/s/browser-test' },
  { name: 'viewer', url: '/s/viewer-session' },
  { name: 'shared-link', url: '/shared/view-token' }
]

async function measure(page) {
  return page.evaluate(() => {
    const bar = document.querySelector('.term-bar')
    if (!bar) return null
    const box = bar.getBoundingClientRect()
    const children = [...bar.children].filter(el => {
      const r = el.getBoundingClientRect()
      return r.width > 0 && r.height > 0 && getComputedStyle(el).position !== 'absolute'
    })
    // Rows are clusters of vertical centres: a control that wrapped sits a full control height
    // lower, while the title block and a button on the same line differ by a few pixels only.
    const centres = children.map(el => { const r = el.getBoundingClientRect(); return r.top + r.height / 2 }).sort((a, b) => a - b)
    const rows = { size: centres.filter((c, i) => i === 0 || c - centres[i - 1] > 14).length }
    const clipped = children.filter(el => el.getBoundingClientRect().right > box.right + 1).map(el => el.className || el.tagName)
    return {
      height: Math.round(box.height),
      rows: rows.size,
      overflowsX: bar.scrollWidth > bar.clientWidth + 1,
      clipped,
      visible: children.map(el => (el.getAttribute('aria-label') || el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40))
    }
  })
}

;(async () => {
  fs.mkdirSync(shots, { recursive: true })
  const browser = process.env.AGENTHUB_CDP_URL
    ? await chromium.connectOverCDP(process.env.AGENTHUB_CDP_URL)
    : await chromium.launch({ executablePath: process.env.CHROME_PATH, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  const context = await browser.newContext({ viewport: { width: 1280, height: 720 } })
  const page = await context.newPage()
  const errors = []
  page.on('pageerror', e => errors.push(e.message))
  const report = []
  for (const view of views) {
    for (const width of widths) {
      await page.setViewportSize({ width, height: 720 })
      await page.goto(appUrl + view.url)
      await page.locator('.term-bar').waitFor()
      await page.locator('[data-open-workspace]').click()
      await page.waitForTimeout(800)
      const m = await measure(page)
      report.push({ label, view: view.name, width, ...m })
      await page.screenshot({ path: path.join(shots, `${label}-${view.name}-${width}.png`), clip: { x: 0, y: 0, width, height: 300 } })
      const menu = page.locator('[data-session-menu-toggle]')
      if (await menu.count()) {
        await menu.click()
        await page.waitForTimeout(200)
        await page.screenshot({ path: path.join(shots, `${label}-${view.name}-${width}-menu.png`), clip: { x: 0, y: 0, width, height: 560 } })
        await page.keyboard.press('Escape')
      }
    }
  }
  fs.writeFileSync(path.join(shots, `${label}-measurements.json`), JSON.stringify({ report, errors }, null, 2))
  console.log(JSON.stringify({ report, errors }, null, 2))
  await context.close()
  await browser.close()
})().catch(e => { console.error(e); process.exit(1) })
