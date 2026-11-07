// Browser proof (#461): this branch's web app (Vite) against the proof host (host/), the session
// agent on 127.0.0.1:27681 (real common/server.js + Codex driver, real Codex CLI as the probe) and
// the recording mock. Drives the new-session dialog in Chromium over CDP:
//   account 1 list → switch the session to account 2 and Refresh → a refused account → an
//   unlisted typed id stays → custom endpoint lookups never reach the endpoint from the backend →
//   a viewer reads the stored list without account ids and may not refresh.
// Usage: node browser-dialog.mjs <cdp url> <app url> <out dir> <dir with auth-acct-*.json> <mock log>
// playwright-core resolves from NODE_PATH.
import fs from 'node:fs';
import { createRequire } from 'node:module';

const require = createRequire(process.env.NODE_PATH + '/');
const { chromium } = require('playwright-core');
const [cdp, app, out, auths, mockLog] = process.argv.slice(2);
const hub = 'http://127.0.0.1:8080';
const pod = 'http://127.0.0.1:27681';
const token = 'proof-callback-token-0123456789';
const results = [];
const check = (name, ok, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); };
const secrets = ['acct-1', 'acct-2', 'acct-deny', token, 'rt-SECRET'];

async function switchAccount(account) {
  // What the hub's account switch does: record the account, then push the login to the pod.
  await fetch(`${hub}/__switch/${account}`, { method: 'POST' });
  const put = await fetch(`${pod}/agenthub/credentials`, { method: 'PUT', headers: { 'X-Agent-Token': token, 'X-Agent-Provider': 'codex',
    'Content-Type': 'application/json' }, body: fs.readFileSync(`${auths}/auth-${account}.json`, 'utf8') });
  return put.status;
}

const browser = await chromium.connectOverCDP(cdp);
// The session browser has one persistent context; pages get their own viewport.
const context = browser.contexts()[0];
const page = await context.newPage();
await page.setViewportSize({ width: 1280, height: 1000 });
const errors = [];
page.on('pageerror', e => errors.push(e.message));
await fetch(`${hub}/__as/alice`, { method: 'POST' });
await page.goto(app);
await page.locator('button:visible', { hasText: '+ Give an agent a task' }).first().click();
await page.click('[data-agent-option="Codex"]');
const card = page.locator('[data-agent-card]');
const options = () => page.locator('[data-model-choice] [data-model-option]').evaluateAll(els => els.map(e => e.getAttribute('data-model-option')));

await page.waitForSelector('[data-model-choice] [data-model-option]');
check('account 1: the dialog lists the account\'s models from the hub', JSON.stringify(await options()) === '["gpt-6.1-sol","gpt-6-luna"]', (await options()).join(','));
check('account 1: the status says it is this account\'s list', /This account's models/.test(await page.textContent('[data-model-choice] [data-model-status]')));
await page.click('[data-model-option="gpt-6-luna"]');
check('choosing a model fills the field', await page.inputValue('[data-model-choice] [data-model-input]') === 'gpt-6-luna');
await card.screenshot({ path: `${out}/1-account-1.png` });

check('account switch on the running session is installed', [200, 202].includes(await switchAccount('acct-2')));
await page.selectOption('[data-account-select]', 'acct-2');
await page.waitForFunction(() => /No list for this account yet/.test(document.querySelector('[data-model-choice] [data-model-status]')?.textContent || ''));
check('account 2 before any probe: unknown, field still free', await page.isEnabled('[data-model-choice] [data-model-input]'));
await page.click('[data-model-choice] [data-model-refresh]');
await page.waitForFunction(() => document.querySelectorAll('[data-model-choice] [data-model-option]').length === 1, null, { timeout: 90000 });
check('account 2 after Refresh: the running session answered for account 2', JSON.stringify(await options()) === '["gpt-6-astra"]', (await options()).join(','));
check('the chosen model stays chosen and is marked as not listed',
  await page.inputValue('[data-model-choice] [data-model-input]') === 'gpt-6-luna' && /kept as typed/.test(await page.textContent('[data-model-unlisted]')));
await card.screenshot({ path: `${out}/2-account-2-refreshed.png` });

await page.selectOption('[data-account-select]', 'acct-1');
await page.waitForFunction(() => document.querySelectorAll('[data-model-choice] [data-model-option]').length === 2);
check('account 1 keeps its own list after account 2 was probed', JSON.stringify(await options()) === '["gpt-6.1-sol","gpt-6-luna"]');

check('switch to a refused account is installed', [200, 202].includes(await switchAccount('acct-deny')));
await page.selectOption('[data-account-select]', 'acct-deny');
await page.click('[data-model-choice] [data-model-refresh]');
await page.waitForFunction(() => /refused/.test(document.querySelector('[data-model-choice] [data-model-status]')?.textContent || ''), null, { timeout: 90000 });
const refused = await page.locator('[data-model-choice] [data-model-status]');
check('a refused account shows a warning, not a list passed off as the account\'s', (await refused.getAttribute('class')).includes('warn'), await refused.textContent());
await card.screenshot({ path: `${out}/3-refused-account.png` });

// Custom endpoint: the dialog asks the hub; the hub must not fetch the endpoint itself.
const before = fs.readFileSync(mockLog, 'utf8').length;
await page.click('[data-auth-option="CustomEndpoint"]');
await page.fill('[data-endpoint-url]', 'http://127.0.0.1:18181/v1');
await page.waitForFunction(() => /No list for this account yet/.test(document.querySelector('[data-endpoint-model] [data-model-status]')?.textContent || ''));
await page.click('[data-endpoint-model] [data-model-refresh]');
await page.waitForSelector('[data-endpoint-model] [data-model-notice]');
check('an endpoint without a running session: the hub says so instead of fetching it',
  /No running session uses this account/.test(await page.textContent('[data-endpoint-model] [data-model-notice]')));
check('the endpoint received no request from the hub', fs.readFileSync(mockLog, 'utf8').length === before);
await card.screenshot({ path: `${out}/4-endpoint-unknown.png` });

const mobile = await context.newPage();
await mobile.setViewportSize({ width: 390, height: 844 });
await mobile.goto(app);
await mobile.locator('button:visible', { hasText: '+ Give an agent a task' }).first().click();
await mobile.click('[data-agent-option="Codex"]');
await mobile.waitForSelector('[data-model-choice] [data-model-option]');
const overflow = await mobile.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
check('390 px: the picker fits without horizontal scrolling', overflow <= 0, `overflow ${overflow}px`);
await mobile.locator('[data-model-choice]').screenshot({ path: `${out}/5-mobile.png` });

// Viewer: bob may read alice's session list, never refresh it, and sees no account.
await fetch(`${hub}/__as/bob`, { method: 'POST' });
const viewer = await fetch(`${hub}/api/sessions/proof-session/models`);
const viewerText = await viewer.text();
check('viewer reads the session\'s stored list', viewer.status === 200 && JSON.parse(viewerText).catalog?.models?.length > 0, viewerText.slice(0, 120));
check('viewer\'s answer names no account, token or login', secrets.every(s => !viewerText.includes(s)));
check('viewer may not refresh', (await fetch(`${hub}/api/sessions/proof-session/models?refresh=true`)).status === 403);
const bobDialog = await (await fetch(`${hub}/api/model-catalog?agent=Codex&authMode=Subscription&credentialId=acct-1`)).json();
check('bob\'s own dialog does not see alice\'s account list', bobDialog.freshness === 'unknown' && bobDialog.catalog === null);
await fetch(`${hub}/__as/alice`, { method: 'POST' });

check('no page errors', errors.length === 0, errors.join(' | '));
await page.close();
await mobile.close();
const failed = results.filter(ok => !ok).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
