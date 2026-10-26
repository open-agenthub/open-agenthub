import assert from 'node:assert/strict';
import test from 'node:test';
import { chromium } from 'playwright-core';
import { BrowserSupervisor, readFrameSelection } from '../supervisor.mjs';
import { connectBrowser } from '../../agent-runtime/browser/connection.mjs';
import { launchBrowser } from './launch-browser.mjs';

// The unit fake cannot expose focus emulation: it supplies exactly one focused tab.
// Run this against a real browser with its initial blank tab still open.
test('real clipboard HTTP requests target the visible tab with both CDP clients connected', {
  skip: !process.env.AGENTHUB_TEST_BROWSER_EXECUTABLE,
  // Covers a slow but healthy run. With its 2 CPUs shared by 112 busy threads, a pod took 7–22 s to
  // CDP and 54–109 s for the whole test (20 runs). A browser that never starts already fails at
  // BROWSER_READY_MS, so this bound only waits that long for a test that is making progress.
  timeout: 240_000,
}, async t => {
  // Undone in reverse: the agent's CDP client, the supervisor (it holds the fixed port 6081, which
  // the next run needs), the browser, then its profile. One hook keeps that order explicit.
  const cleanup = [];
  t.after(async () => {
    for (const step of cleanup.reverse()) {
      try { await step(); } catch (error) { t.diagnostic(`cleanup: ${error.message}`); }
    }
  });
  const endpoint = await launchBrowser(t, cleanup);
  const supervisor = new BrowserSupervisor({ cdpUrl: endpoint });
  cleanup.push(() => supervisor.shutdown());
  await supervisor.start();
  const background = supervisor.context.pages()[0];
  const page = await supervisor.context.newPage();
  await page.setContent('<textarea></textarea><iframe srcdoc="<textarea></textarea>"></iframe>');
  await page.bringToFront();
  await page.locator('textarea').click();

  const exercise = async locator => {
    await locator.click();
    await locator.fill('');
    const response = await fetch('http://127.0.0.1:6081/clipboard/paste', {
      method: 'POST', body: JSON.stringify({ text: 'local text\nsecond line' }),
    });
    assert.equal(response.status, 204);
    assert.equal(await locator.inputValue(), 'local text\nsecond line');
    await locator.evaluate(element => element.select());
    for (const cut of [false, true]) {
      const copied = await fetch('http://127.0.0.1:6081/clipboard/copy', {
        method: 'POST', body: JSON.stringify({ cut }),
      });
      assert.equal(copied.status, 200);
      assert.deepEqual(await copied.json(), { text: 'local text\nsecond line' });
    }
    assert.equal(await locator.inputValue(), '');
    assert.equal((await background.evaluate(readFrameSelection)).visible, false);
  };

  await exercise(page.locator('textarea'));
  const agent = await connectBrowser(chromium, endpoint);
  cleanup.push(() => agent.close());
  await exercise(page.locator('textarea'));
  await exercise(page.frameLocator('iframe').locator('textarea'));
});
