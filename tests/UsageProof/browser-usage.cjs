// Screenshots the real Usage and Home pages in the session's Chromium (over CDP) for the branch UI
// and for origin/main's UI, each served by Vite in front of a proof host that real CLIs reported to.
//   node browser-usage.cjs <cdp> <branch-url> <main-url> <outdir>
const { chromium } = require(process.env.PW || 'playwright-core');
(async () => {
  const [cdp, branchUrl, mainUrl, out] = process.argv.slice(2);
  const browser = await chromium.connectOverCDP(cdp);
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  // The session browser reaches one allow-listed port, so each UI can be served in turn: an empty
  // url skips that side.
  for (const [name, url] of [['branch', branchUrl], ['main', mainUrl]].filter(([, u]) => u && u !== '-')) {
    await page.goto(url + '/', { waitUntil: 'networkidle' });
    await page.click('text=Usage & cost');
    await page.waitForSelector('.usage-page .trow', { timeout: 15000 });
    await page.screenshot({ path: `${out}/usage-${name}.png`, fullPage: true });
    const rows = await page.$$eval('.trow', rs => rs.map(r => r.innerText.replace(/\s+/g, ' ')));
    const stats = await page.$$eval('.stat', rs => rs.map(r => r.innerText.replace(/\s+/g, ' ')));
    console.log(`== ${name} stats\n` + stats.join('\n') + `\n== ${name} rows\n` + rows.join('\n'));
    await page.goto(url + '/', { waitUntil: 'networkidle' });
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${out}/home-${name}.png` });
  }
  console.log('page errors:', errors.length ? errors : 'none');
  await context.close();
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
