// #236: counts Chromium's top-level windows after each way a page or a CDP client can open one.
// Run against the browser runtime's real entrypoint (CDP on 127.0.0.1:9223, supervisor attached).
// usage: node windows.mjs <playwright-core dir>
import { createRequire } from 'node:module';
import { startFixtures } from './fixtures.mjs';

const require = createRequire(import.meta.url);
const { chromium } = require(process.argv[2]);
const cdp = 'http://127.0.0.1:9223';

async function windows(raw) {
  const { targetInfos } = await raw.send('Target.getTargets');
  const byWindow = new Map();
  for (const target of targetInfos.filter(t => t.type === 'page')) {
    const { windowId } = await raw.send('Browser.getWindowForTarget', { targetId: target.targetId });
    byWindow.set(windowId, [...(byWindow.get(windowId) ?? []), target.url.replace(/^http:\/\/127\.0\.0\.1:\d+/, '')]);
  }
  return Object.fromEntries(byWindow);
}

const fixtures = await startFixtures();
const browser = await chromium.connectOverCDP(cdp, { noDefaults: true });
const raw = await browser.newBrowserCDPSession();
const context = browser.contexts()[0];
const page = context.pages()[0];
await page.goto(`${fixtures.base}/`);
const settle = () => new Promise(resolve => setTimeout(resolve, 1500));
const results = [];
const record = async step => {
  await settle();
  const state = await windows(raw);
  results.push({ step, windows: Object.keys(state).length, layout: state });
  console.log(JSON.stringify(results.at(-1)));
};

await record('start');
for (const id of ['blank', 'plain-open', 'popup']) {
  await page.bringToFront();
  await page.click(`#${id}`);
  await record(`click #${id}`);
}
// Shift+click is how a viewer asks Chromium for a new window; no window.open is involved.
await page.bringToFront();
await page.click('#blank', { modifiers: ['Shift'] });
await record('shift+click link');
await raw.send('Target.createTarget', { url: `${fixtures.base}/target?cdp-new-window`, newWindow: true });
await record('CDP Target.createTarget newWindow:true');
await raw.send('Target.createTarget', { url: `${fixtures.base}/target?cdp-tab` });
await record('CDP Target.createTarget');

// An OAuth-style popup must still reach its opener when it lands in a tab.
const opener = await context.newPage();
await opener.goto(`${fixtures.base}/opener`);
await opener.click('#oauth');
await opener.waitForFunction(() => document.title.startsWith('got:'), null, { timeout: 10_000 })
  .catch(() => undefined);
console.log(JSON.stringify({ step: 'oauth popup posts to opener', title: await opener.title() }));
await record('after oauth popup');

const failures = results.filter(result => result.windows !== 1).map(result => result.step);
console.log(JSON.stringify({ verdict: failures.length ? 'FAIL' : 'PASS', extraWindowAfter: failures }));
await raw.detach();
await browser.close();
fixtures.server.close();
