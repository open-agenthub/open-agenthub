const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const deps = createRequire(process.env.PROOF_DEPS + '/package.json');
(async () => {
 const { createServer } = await import(root + '/frontend/node_modules/vite/dist/node/index.js');
 const html = root + '/frontend/proof251.html', entry = root + '/frontend/proof251-entry.js';
 fs.writeFileSync(html, '<html><body><div id="app"></div><script type="module" src="/proof251-entry.js"></script></body></html>');
 fs.writeFileSync(entry, `import { createApp } from 'vue';
 import TerminalView from './src/components/TerminalView.vue'; import './src/style.css';
 createApp(TerminalView, { initialUi: 'workspace', session: { id: 's251', title: 'Long conversation proof', phase: 'Running', mode: 'Interactive', agent: 'Claude', uiMode: 'terminal', accessRole: 'Owner', createdAt: new Date().toISOString() } }).mount('#app');`);
 const server = await createServer({ root: root + '/frontend', server: { port: 19253, strictPort: true } });
 await server.listen();
 const browser = await deps('playwright-core').chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
 const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
 const errors = []; page.on('pageerror', e => errors.push(e.message));
 let mode = process.env.LIVE ? 'conversation' : 'long';
 const queries = [];
 await page.route('**/api/**', async route => {
  const url = new URL(route.request().url());
  if (url.pathname.endsWith('/conversation')) {
   queries.push(url.search);
   const r = await fetch('http://127.0.0.1:19252/' + mode + url.search);
   return route.fulfill({ status: r.status, contentType: 'application/json', body: await r.text() });
  }
  const body = /permissions|messages|credentials|accounts|secrets|files/.test(url.pathname) ? [] : {};
  await route.fulfill({ json: body });
 });
 const start = Date.now();
 await page.goto('http://localhost:19253/proof251.html');
 if (mode === 'long') {
  await page.getByText('History message 19999:', { exact: false }).waitFor();
  const initialMs = Date.now() - start;
  assert.equal(await page.locator('[data-workspace-role]').count(), 500);
  assert.equal(queries.length, 1);
  assert.equal(queries[0], '?offset=-1');
  assert.equal(await page.getByText('History message 00000:', { exact: false }).count(), 0);
  const scroller = page.locator('.conversation-scroll');
  await scroller.evaluate(el => { el.scrollTop = 0; });
  await page.locator('[data-load-earlier]').click();
  await page.getByText('History message 19000:', { exact: false }).waitFor();
  assert.equal(await page.locator('[data-workspace-role]').count(), 1000);
  assert(queries.includes('?offset=19000&max=500'));
  console.log(JSON.stringify({ initialMs, initialMessages: 500, afterEarlier: 1000, queries, errors }));
 } else {
  await page.getByText('LATER-TURN-251-3', { exact: false }).first().waitFor({ timeout: 90000 });
  await page.reload();
  await page.getByText('LATER-TURN-251-3', { exact: false }).first().waitFor();
  console.log(JSON.stringify({ live: true, queries, errors }));
 }
 assert.deepEqual(errors, []);
 await page.screenshot({ path: process.env.PROOF_SCREENSHOT || '/tmp/251-browser.png' });
 await browser.close(); await server.close(); fs.unlinkSync(html); fs.unlinkSync(entry);
})().catch(e => { console.error(e); process.exit(1); });
