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
// 390 is a phone: there the bar splits into two rows on purpose (title and ⋯, then the controls).
const widths = [1280, 1024, 768, 390]
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
  // Keyboard and the menu's actions, owner at 1280 (only once the menu exists).
  const keyboard = {}
  await page.setViewportSize({ width: 1280, height: 720 })
  await page.goto(appUrl + '/s/browser-test')
  await page.locator('.term-bar').waitFor()
  await page.waitForTimeout(800)
  const toggle = page.locator('[data-session-menu-toggle]')
  if (await toggle.count()) {
    const active = () => page.evaluate(() => {
      const el = document.activeElement
      if (!el) return null
      const hook = [...el.attributes].map(a => a.name).find(n => n.startsWith('data-') && n !== 'data-menu-closes' && !n.startsWith('data-v-'))
      return `${hook || el.tagName} "${(el.tagName === 'SELECT' ? '' : el.textContent).trim().slice(0, 30)}"`
    })
    await toggle.focus()
    await page.keyboard.press('Enter')
    await page.waitForTimeout(100)
    keyboard.afterEnter = { expanded: await toggle.getAttribute('aria-expanded'), focus: await active() }
    await page.keyboard.press('Tab')
    keyboard.afterTab = await active()
    await page.keyboard.press('ArrowDown')
    keyboard.afterArrowDown = await active()
    await page.keyboard.press('Escape')
    keyboard.afterEscape = { expanded: await toggle.getAttribute('aria-expanded'), focus: await active() }
    await toggle.click()
    await page.locator('[data-send-message-toggle]').click()
    keyboard.messagesPanelVisible = await page.locator('[data-send-message]').isVisible()
    keyboard.menuClosedAfterItem = await toggle.getAttribute('aria-expanded')
    await page.locator('[data-send-message-close]').click()
    await toggle.click()
    await page.locator('[data-provider-switch-open]').click()
    await page.waitForTimeout(300)
    keyboard.providerPanelVisible = await page.locator('[data-provider-panel]').isVisible()
    await page.screenshot({ path: path.join(shots, `${label}-owner-1280-provider.png`), clip: { x: 0, y: 0, width: 1280, height: 400 } })
    keyboard.connection = await page.locator('[data-connection-status]').getAttribute('aria-label')
  }
  fs.writeFileSync(path.join(shots, `${label}-measurements.json`), JSON.stringify({ report, keyboard, errors }, null, 2))
  console.log(JSON.stringify({ report, keyboard, errors }, null, 2))
  await context.close()
  await browser.close()
})().catch(e => { console.error(e); process.exit(1) })
