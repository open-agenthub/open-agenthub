const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const origin = process.env.UI_URL || 'http://localhost:5173';
const output = process.env.OUTPUT_DIR || fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'companion-overflow-proof-'));
const phase = process.env.PHASE || 'after';
const token = 'segment'.repeat(60);
const sentence = 'This preview line is long ordinary prose that has to wrap at the preview edge. '.repeat(6);
const cells = n => Array.from({ length: n }, (_, i) => `column-${i}-${'wide'.repeat(6)}`);
const prose = 'Gründungsberatung, Mentoring und Antragsvorbereitung für das Programm';
const document = `# Wide document\n\n| Was das Programm bringt | Unsere Bewertung aus der aktuellen Lage |\n| --- | --- |\n| ${prose} | ${prose} |\n\n## Second\n\n${sentence}\n\nhttps://example.com/${token}\n\nInline \`${token}\`\n\n` +
  `| ${cells(14).join(' | ')} |\n|${' --- |'.repeat(14)}\n| ${cells(14).join(' | ')} |\n\n\`\`\`text\n${token}\n\`\`\`\n`;
const chat = `Chat prose with a long link https://example.com/${token}\n\n| A | B |\n| --- | --- |\n| ${token} | ${token} |`;
const entries = [{ role: 'user', text: chat }, { role: 'assistant', text: chat }];
const file = { id: 'f1', name: 'Beratungsgespräch Gründungsservice Halle (04.11.2026).md', mimeType: 'text/markdown', state: 'Ready', size: document.length, createdAt: '2026-11-04T08:00:00Z' };

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined, args: ['--no-sandbox'], ignoreDefaultArgs: ['--hide-scrollbars'] });
  const results = [], errors = [];
  try {
    const page = await browser.newPage();
    page.on('pageerror', error => errors.push(error.message));
    // Production components, layout and markdown renderer; only the session transport is synthetic.
    const session = { id: 'pane-test', title: 'Companion overflow', agent: 'Codex', phase: 'Running', mode: 'Interactive', uiMode: 'terminal', accessRole: 'Owner', browser: { phase: 'Stopped' } };
    await page.routeWebSocket('**/ws/**', () => {});
    await page.route('**/api/**', route => {
      const p = new URL(route.request().url()).pathname;
      if (p.endsWith('/files/f1/content')) return route.fulfill({ body: document, contentType: 'text/markdown' });
      let json = [];
      if (p === '/api/config') json = { authority: '' };
      else if (p === '/api/sessions') json = [session];
      else if (p.endsWith('/conversation')) json = { source: 'native', offset: 0, nextOffset: entries.length, length: entries.length, entries };
      else if (p.endsWith('/presentation')) json = { revision: 0, fileId: null };
      else if (p.endsWith('/files/capabilities')) json = { canUpload: false, canRead: true };
      else if (p.endsWith('/files')) json = [file];
      else if (p === '/api/agents/allowed') json = { agents: ['Codex'] };
      else if (p === '/api/admin/access') json = { isAdmin: false };
      else if (p === '/api/credentials' || p === '/api/credentials/accounts') json = {};
      return route.fulfill({ json });
    });
    for (const width of [1280, 1440, 1920, 900, 390]) for (const split of ['Home', 'default', 'End']) {
      if (width === 390 && split !== 'default') continue;
      await page.setViewportSize({ width, height: 900 });
      await page.goto(origin + '/s/pane-test');
      await page.locator('[data-workspace-role="assistant"]').waitFor();
      await page.locator('[data-open-files]').click();
      await page.locator('[data-file-preview="markdown"] table').first().waitFor();
      await page.evaluate(() => document.fonts.ready);
      const narrow = !(await page.locator('.split-seam').isVisible());
      if (split !== 'default') { await page.locator('.split-seam').focus(); await page.keyboard.press(split); await page.waitForTimeout(100); }
      // The narrow layout shows one pane at a time; the Files tab is the one that could overflow.
      const result = await page.evaluate(() => {
        const side = document.querySelector('.terminal-side').getBoundingClientRect();
        const seam = document.querySelector('.split-seam')?.getBoundingClientRect();
        const pane = document.querySelector('.companion-side');
        const probes = [];
        if (side.width) for (let y = side.top + 20; y < side.bottom - 4; y += 40)
          for (const x of [side.left + 2, side.left + 30, side.left + side.width / 2, side.right - 4]) probes.push([x, y]);
        if (seam?.width) for (let y = seam.top + 20; y < seam.bottom; y += 40) probes.push([seam.left + seam.width / 2, y]);
        // elementsFromPoint, not elementFromPoint: the conversation scroller wins the top hit even
        // where it is transparent, so the pane underneath only shows up further down the stack.
        const leaks = probes.flatMap(([x, y]) => document.elementsFromPoint(x, y).filter(el => pane.contains(el))
          .slice(0, 1).map(el => ({ x: Math.round(x), y: Math.round(y), hit: `${el.tagName}.${el.className}` })));
        const preview = document.querySelector('.preview');
        const table = [...document.querySelectorAll('[data-file-preview="markdown"] .md-table')].pop();
        const paneRect = pane.getBoundingClientRect();
        const widest = Math.max(...[...pane.querySelectorAll('*')].map(el => el.getBoundingClientRect().right));
        const boxes = Object.fromEntries(['.companion-side', '.companion-body', '.files-companion', '.files-pane', '.file-main', '.preview']
          .map(sel => [sel, Math.round(document.querySelector(sel).getBoundingClientRect().right)]));
        return {
          boxes, probes: probes.length, leaks,
          terminalBackground: getComputedStyle(document.querySelector('.terminal-side')).backgroundColor,
          paneRight: Math.round(paneRect.right), widestPaneBox: Math.max(...Object.values(boxes)), widestDescendantRight: Math.round(widest),
          paneOverflowX: getComputedStyle(pane).overflowX,
          preview: { client: preview.clientWidth, scroll: preview.scrollWidth },
          table: { client: table.clientWidth, scroll: table.scrollWidth, overflow: getComputedStyle(table).overflowX },
          document: { client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth },
        };
      });
      results.push({ width, split, narrow, ...result });
      await page.screenshot({ path: path.join(output, `${phase}-files-${width}-${split}.png`) });
    }
    fs.writeFileSync(path.join(output, `${phase}.json`), JSON.stringify(results, null, 2));
    console.table(results.map(r => ({ width: r.width, split: r.split, files: r.boxes['.files-pane'] - r.boxes['.companion-side'], probes: r.probes, leaks: r.leaks.length, bg: r.terminalBackground,
      pane: `${r.paneRight}/${r.widestDescendantRight}`, preview: `${r.preview.scroll}/${r.preview.client}`, table: `${r.table.scroll}/${r.table.client}`, doc: `${r.document.scroll}/${r.document.client}` })));
    if (phase !== 'before') for (const r of results) {
      assert(Object.values(r.boxes).every(right => right <= r.boxes['.companion-side'] + 1), `${r.width}/${r.split}: a pane box is wider than the companion column`);
      assert.deepEqual(r.leaks, [], `${r.width}/${r.split}: companion content is hit inside the conversation column or seam`);
      assert.notEqual(r.terminalBackground, 'rgba(0, 0, 0, 0)', `${r.width}: conversation column is transparent`);
      // At a 25% split of a 900 px window the 150 px file list leaves the preview a few pixels;
      // only its padding can overflow there, which is not what this check is about.
      if (r.preview.client >= 120) assert(r.preview.scroll <= r.preview.client, `${r.width}/${r.split}: the preview itself scrolls sideways`);
      assert.equal(r.table.overflow, 'auto');
      assert(r.table.scroll > r.table.client, `${r.width}: the wide table scrolls inside its own wrapper`);
      // The page itself can still overflow by the composer in a very narrow conversation column
      // (as on v1.12.1); the companion must not be what pushes past the viewport.
      assert(r.widestPaneBox <= r.document.client, `${r.width}/${r.split}: companion past the viewport`);
    }
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
