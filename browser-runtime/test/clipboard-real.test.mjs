import assert from 'node:assert/strict';
import test from 'node:test';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright-core';
import { BrowserSupervisor, readFrameSelection } from '../supervisor.mjs';
import { connectBrowser } from '../../agent-runtime/browser/connection.mjs';

// The unit fake cannot expose focus emulation: it supplies exactly one focused tab.
// Run this against a real browser with its initial blank tab still open.
test('real clipboard HTTP requests target the visible tab with both CDP clients connected', {
  skip: !process.env.AGENTHUB_TEST_BROWSER_EXECUTABLE,
  timeout: 60_000,
}, async t => {
  const profile = await mkdtemp(join(tmpdir(), 'browser-clipboard-'));
  const child = spawn(process.env.AGENTHUB_TEST_BROWSER_EXECUTABLE, [
    '--no-sandbox', '--no-first-run', '--no-default-browser-check', '--disable-gpu',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank',
  ], { stdio: 'ignore' });
  t.after(async () => {
    child.kill('SIGKILL');
    await new Promise(resolve => child.exitCode !== null || child.signalCode !== null
      ? resolve() : child.once('exit', resolve));
    await rm(profile, { recursive: true, force: true });
  });
  let port;
  for (let attempt = 0; attempt < 100; attempt += 1) {
    try { port = Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); break; }
    catch {
      if (child.exitCode !== null) throw new Error('Test browser exited before CDP became ready');
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
  assert.ok(port, 'Test browser must expose CDP');
  const endpoint = `http://127.0.0.1:${port}`;
  const supervisor = new BrowserSupervisor({ cdpUrl: endpoint });
  await supervisor.start();
  t.after(() => supervisor.shutdown());
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
  t.after(() => agent.close());
  await exercise(page.locator('textarea'));
  await exercise(page.frameLocator('iframe').locator('textarea'));
});
