const { chromium } = require('../../agent-runtime/session-agent/node_modules/playwright-core');
const assert = require('node:assert/strict');
(async () => {
  const browser = await chromium.connectOverCDP(process.env.AGENTHUB_CDP_URL);
  const context = await browser.newContext();
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  await page.goto(process.env.AGENTHUB_UI_URL + '/s/diagram-proof');
  const open = page.locator('[data-open-workspace]');
  if (await open.isVisible()) await open.click();
  await page.locator('.md-mermaid-svg svg').nth(1).waitFor({ timeout: 60000 });
  const diagrams = await page.locator('.md-mermaid-svg svg').evaluateAll(xs => xs.map(x => ({ text: x.textContent, width: x.getBoundingClientRect().width, height: x.getBoundingClientRect().height })));
  assert.equal(diagrams.length, 2);
  assert(diagrams.every(d => d.width > 0 && d.height > 0));
  assert(diagrams[0].text.includes('Request'));
  assert(diagrams[1].text.includes('Client') && diagrams[1].text.includes('Server'));
  assert.deepEqual(errors, []);
  if (process.env.AGENTHUB_SCREENSHOT)
    await page.screenshot({ path: process.env.AGENTHUB_SCREENSHOT, fullPage: true });
  console.log(JSON.stringify({ diagrams: diagrams.map(d => ({ width: d.width, height: d.height })), errors }));
  await context.close();
  await browser.close();
})().catch(e => { console.error(e); process.exitCode = 1; });
