// Real Workspace integration with the merged session menu. API responses are explicit fixtures;
// native delivery and repository restoration are proved separately on the actual worker.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('/opt/session-agent/node_modules/playwright-core');
(async () => {
  const browser = await chromium.connectOverCDP(process.env.PROOF_CDP);
  const page = await browser.contexts()[0].newPage();
  const out = process.env.PROOF_OUT;
  fs.mkdirSync(out, { recursive: true });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const manifest = { reviewHash: 'repository-hash', repositories: [{ index: 0, destination: 'repo',
    branch: 'work/proof', commit: 'a'.repeat(40), trackedChanges: 0, untracked: 0, unpushed: 0,
    ignored: { count: 1, names: ['.env'], omitted: 0 }, error: null }],
    outside: { count: 1, names: ['scratch.txt'], omitted: 0 } };
  const selections = [];
  await page.route('**/api/sessions/browser-test/provider-switch**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    let body;
    if (url.pathname.endsWith('/repositories')) body = manifest;
    else if (url.pathname.endsWith('/preview')) {
      const selection = request.postDataJSON(); selections.push(selection);
      body = { text: 'Provider handover to Claude.\nOmitted: native state.\n' + selection.handover.task,
        reviewHash: 'review-hash', target: selection.target,
        capacity: { windowTokens: 100000, source: 'native-minimum' }, policy: 'Existing restrictions remain in effect.' };
    } else body = { available: true, repositoryBacked: true, operation: null };
    await route.fulfill({ json: body });
  });
  try {
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(process.env.PROOF_URL + '/s/browser-test');
      await page.locator('.term-bar').waitFor();
      await page.locator('[data-session-menu-toggle]').click();
      await page.locator('[data-provider-switch-open]').click();
      await page.locator('[data-provider-panel]').waitFor();
      assert.equal(await page.locator('[data-session-menu-toggle]').getAttribute('aria-expanded'), 'false');
      await page.getByRole('button', { name: 'Check again', exact: true }).click();
      await page.getByText('.env', { exact: true }).waitFor();
      assert(await page.getByRole('button', { name: 'Review exact handover' }).isDisabled());
      await page.getByLabel(/I understand that ignored files/).check();
      await page.getByLabel('Instructions to preserve').fill('Keep existing restrictions.');
      await page.getByLabel('Task to continue').fill('Continue the repository task.');
      await page.getByRole('button', { name: 'Review exact handover' }).click();
      await page.locator('[data-provider-review] pre').waitFor();
      assert.match(await page.locator('[data-provider-review] pre').innerText(), /Continue the repository task/);
      assert(await page.evaluate(() => {
        const box = document.querySelector('[data-provider-panel]').getBoundingClientRect();
        return box.left >= 0 && box.right <= innerWidth && document.documentElement.scrollWidth <= innerWidth;
      }));
      await page.screenshot({ path: path.join(out, `repository-menu-${width}.png`) });
    }
    assert.equal(selections.length, 2);
    assert(selections.every(selection => selection.repositoryHash === 'repository-hash' && selection.acknowledgeOmissions));
    assert.deepEqual(errors, []);
    console.log('PASS: actual merged Workspace ⋯ menu → repository checks, ignored/outside acknowledgement and handover review in Chromium at 1280 and 390px.');
  } finally { await page.close(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
