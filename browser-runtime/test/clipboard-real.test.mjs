import assert from 'node:assert/strict';
import test from 'node:test';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright-core';
import { BrowserSupervisor, readFrameSelection } from '../supervisor.mjs';
import { connectBrowser } from '../../agent-runtime/browser/connection.mjs';

// A CI runner once needed more than the 10 s the first version waited for the browser to write
// DevToolsActivePort (#214), and the failure said nothing about why. The wait is a wall-clock
// deadline on what the supervisor actually needs: the port file appears seconds before CDP
// answers /json/version (2.4 s and 4.5 s on a loaded pod). A failure says how long it waited
// and what the browser printed.
const BROWSER_READY_MS = 45_000;
const STDERR_TAIL = 8 * 1024;

/** Starts the test browser and resolves its CDP endpoint once /json/version answers. */
async function launchBrowser(t, cleanup) {
  const profile = await mkdtemp(join(tmpdir(), 'browser-clipboard-'));
  cleanup.push(() => rm(profile, { recursive: true, force: true }));
  const child = spawn(process.env.AGENTHUB_TEST_BROWSER_EXECUTABLE, [
    '--no-sandbox', '--no-first-run', '--no-default-browser-check', '--disable-gpu',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank',
  ], { stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-STDERR_TAIL); });
  const exited = new Promise(resolve => child.once('exit', resolve));
  cleanup.push(async () => {
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    await exited;
  });

  const started = Date.now();
  const failure = reason => new Error(`${reason} after ${Date.now() - started} ms. Browser stderr (last ${STDERR_TAIL} bytes):\n${stderr || '(empty)'}`);
  while (Date.now() - started < BROWSER_READY_MS) {
    if (child.exitCode !== null || child.signalCode !== null) {
      throw failure(`Test browser exited (${child.exitCode ?? child.signalCode}) before CDP became ready`);
    }
    try {
      const port = Number((await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]);
      const endpoint = `http://127.0.0.1:${port}`;
      if (port && (await fetch(endpoint + '/json/version', { signal: AbortSignal.timeout(2_000) })).ok) {
        // In the log of every run, so a slow runner shows how close it came to the deadline.
        t.diagnostic(`browser CDP ready after ${Date.now() - started} ms`);
        return endpoint;
      }
    } catch {
      // Not written yet, or CDP not listening yet: both are what the deadline is for.
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw failure('Test browser did not answer /json/version');
}

// The unit fake cannot expose focus emulation: it supplies exactly one focused tab.
// Run this against a real browser with its initial blank tab still open.
test('real clipboard HTTP requests target the visible tab with both CDP clients connected', {
  skip: !process.env.AGENTHUB_TEST_BROWSER_EXECUTABLE,
  // The browser's deadline plus the clipboard round trips, which took up to 20 s on a pod whose
  // CPU was shared with 112 busy threads.
  timeout: 120_000,
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
