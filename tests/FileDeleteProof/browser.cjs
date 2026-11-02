// Real Chromium against the real frontend and the proof host: upload a file in the Files pane,
// delete it through the in-row confirmation, and check it is gone from the list and the bucket.
// Usage: node browser.cjs <cdp endpoint> <frontend url reachable from the browser> <screenshot dir>
const { chromium } = require((process.env.PROOF_RUNTIME || '/opt/session-agent') + '/node_modules/playwright-core');
const assert = require('node:assert/strict');
const path = require('node:path');

const host = process.env.PROOF_URL || 'http://127.0.0.1:8080';
const [cdp, app, shots] = process.argv.slice(2);
const stored = async name => (await (await fetch(`${host}/__objects`)).json()).filter(key => key.endsWith('/' + name));

(async () => {
  await fetch(`${host}/__as/alice`, { method: 'POST' });
  const name = `delete-proof-${Date.now()}.txt`;
  const browser = await chromium.connectOverCDP(cdp);
  const page = await browser.contexts()[0].newPage();
  await page.setViewportSize({ width: 1400, height: 800 });
  const dialogs = [], deletes = [];
  page.on('dialog', async dialog => { dialogs.push(dialog.message()); await dialog.dismiss(); });
  page.on('request', request => { if (request.method() === 'DELETE') deletes.push(new URL(request.url()).pathname); });

  await page.goto(`${app}/s/s1`);
  await page.getByRole('button', { name: 'Files', exact: true }).click();
  await page.locator('[data-files-input]').setInputFiles({ name, mimeType: 'text/plain', buffer: Buffer.from('delete me\n') });
  const row = page.locator('.file-row', { hasText: name });
  await row.waitFor();
  assert.equal((await stored(name)).length, 1, 'the upload is in the bucket');
  await page.screenshot({ path: path.join(shots, '1-uploaded.png') });
  console.log('PASS uploaded through the Files pane; object in storage:', (await stored(name))[0]);

  await row.locator('[data-file-delete]').click();
  const confirmation = page.locator('[data-file-delete-confirm]');
  await confirmation.waitFor();
  assert.ok((await confirmation.innerText()).includes(`Delete ${name}?`), 'the question names the file');
  assert.equal(await confirmation.locator('[data-file-delete-project]').count(), 1, 'project note shown');
  await page.screenshot({ path: path.join(shots, '2-confirm.png') });
  assert.deepEqual(deletes, [], 'nothing deleted before confirming');

  await confirmation.locator('[data-file-delete-keep]').click();
  await confirmation.waitFor({ state: 'detached' });
  assert.deepEqual(deletes, []);
  console.log('PASS in-page confirmation with project note; Keep closes it without a request');

  await row.locator('[data-file-delete]').click();
  await page.locator('[data-file-delete-confirm-button]').click();
  await row.waitFor({ state: 'detached' });
  await page.screenshot({ path: path.join(shots, '3-deleted.png') });
  assert.equal(deletes.length, 1);
  assert.match(deletes[0], /^\/api\/sessions\/s1\/files\/[0-9a-f]{32}$/);
  assert.deepEqual(await stored(name), [], 'object gone from storage');
  const listed = await (await fetch(`${host}/api/sessions/s1/files`)).json();
  assert.ok(!listed.some(file => file.name === name), 'not listed by the backend');
  assert.deepEqual(dialogs, [], 'no native dialog');
  console.log('PASS deleted: row gone, backend list without it, object gone from storage, no native dialog; DELETE', deletes[0]);
  await page.close();
  await browser.close();
})().catch(error => { console.error(error); process.exitCode = 1; });
