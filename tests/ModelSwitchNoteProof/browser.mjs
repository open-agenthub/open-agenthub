// Opens the Workspace of the stand-in session in Chromium over CDP, prints what its conversation
// shows (each turn's role and text, notes) and saves a screenshot.
//
// usage: CDP=<endpoint> UI=<web app origin> node browser.mjs <screenshot.png>
import { createRequire } from 'node:module'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_CORE || '/opt/session-agent/node_modules/playwright-core')

const browser = await chromium.connectOverCDP(process.env.CDP)
const context = browser.contexts()[0] || await browser.newContext()
const page = await context.newPage()
try {
  await page.setViewportSize({ width: 1280, height: 800 })
  await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
  await page.goto(process.env.UI + '/s/s1')
  await page.waitForSelector('[data-workspace-conversation] .message', { timeout: 30000 })
  await page.waitForTimeout(1000)
  const shown = await page.$$eval('[data-workspace-conversation] .timeline > *', nodes => nodes.map(node => ({
    role: node.getAttribute('data-workspace-role') || (node.hasAttribute('data-conversation-note') ? 'note' : node.tagName.toLowerCase()),
    // A note's own text, without the time beside it.
    text: (node.hasAttribute('data-conversation-note') ? node.firstChild.textContent : node.innerText).replace(/\s+/g, ' ').trim().slice(0, 200)
  })))
  for (const item of shown) console.log(JSON.stringify(item))
  const users = shown.filter(item => item.role === 'user')
  console.log(JSON.stringify({
    userTurns: users.length,
    userTurnsWithCommandMarkup: users.filter(item => /<command-name>|<local-command-stdout>/.test(item.text)).length,
    // innerText drops the ESC byte itself; what stays visible is the "[2m" of each SGR code.
    userTurnsWithEscapes: users.filter(item => /\x1b|\[\d+m/.test(item.text)).length,
    notes: shown.filter(item => item.role === 'note').map(item => item.text)
  }))
  // The switch sits third in the conversation; the Workspace scrolls to the latest turn.
  await page.$eval('[data-workspace-conversation] .timeline > :nth-child(3)', node => node.scrollIntoView({ block: 'center' }))
  await page.waitForTimeout(300)
  await page.screenshot({ path: process.argv[2] })
} finally {
  await page.close()
  await browser.close()
}
