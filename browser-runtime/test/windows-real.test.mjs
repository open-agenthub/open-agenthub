import assert from 'node:assert/strict';
import test from 'node:test';
import http from 'node:http';
import { chromium } from 'playwright-core';
import { BrowserSupervisor } from '../supervisor.mjs';
import { connectBrowser } from '../../agent-runtime/browser/connection.mjs';
import { launchBrowser } from './launch-browser.mjs';

const pages = {
  '/': `<button id="popup" onclick="window.open('/oauth','oauth','popup,width=500,height=600')">sign in</button>
    <a id="link" href="/target" target="_blank">link</a>
    <script>addEventListener('message', event => { document.title = event.data; });</script>`,
  '/oauth': `<script>window.opener.postMessage('signed-in', '*');</script>`,
  '/target': 'target',
};

// A headless shell has no windows to count, so this runs only against the real browser.
test('popups, new-window targets and shift-clicks all open as tabs of the one window', {
  skip: !process.env.AGENTHUB_TEST_BROWSER_EXECUTABLE,
  timeout: 240_000,
}, async t => {
  const cleanup = [];
  t.after(async () => {
    for (const step of cleanup.reverse()) {
      try { await step(); } catch (error) { t.diagnostic(`cleanup: ${error.message}`); }
    }
  });
  const server = http.createServer((request, response) => {
    const body = pages[new URL(request.url, 'http://x').pathname];
    response.writeHead(body ? 200 : 404, { 'Content-Type': 'text/html' });
    response.end(body ?? '');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  cleanup.push(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;

  const endpoint = await launchBrowser(t, cleanup);
  const supervisor = new BrowserSupervisor({ cdpUrl: endpoint });
  cleanup.push(() => supervisor.shutdown());
  await supervisor.start();
  // The agent's connection, opened as the browser MCP opens it.
  const agent = await connectBrowser(chromium, endpoint);
  cleanup.push(() => agent.close());
  const context = agent.contexts()[0];
  const cdp = await agent.newBrowserCDPSession();

  const count = async () => {
    const { targetInfos } = await cdp.send('Target.getTargets');
    const ids = new Set();
    for (const target of targetInfos.filter(info => info.type === 'page'))
      ids.add((await cdp.send('Browser.getWindowForTarget', { targetId: target.targetId })).windowId);
    return ids.size;
  };
  // A second window may exist for a moment before the supervisor folds it; one that stays fails.
  const windows = async () => {
    const deadline = Date.now() + 10_000;
    while (Date.now() < deadline && await count() !== 1)
      await new Promise(resolve => setTimeout(resolve, 100));
    return count();
  };
  const page = context.pages()[0];
  await page.goto(`${base}/`);

  await page.click('#popup');
  await page.waitForFunction(() => document.title === 'signed-in');
  assert.equal(await windows(), 1, 'popup with window features');

  await page.bringToFront();
  await page.click('#link', { modifiers: ['Shift'] });
  await context.waitForEvent('page');
  assert.equal(await windows(), 1, 'shift+click');

  await cdp.send('Target.createTarget', { url: `${base}/target`, newWindow: true });
  await context.waitForEvent('page');
  assert.equal(await windows(), 1, 'Target.createTarget with newWindow');
  assert.ok(context.pages().some(candidate => candidate.url() === `${base}/target`));
});
