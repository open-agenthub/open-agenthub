const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright-core');
const origin = process.env.UI_URL || 'http://localhost:5173';
const output = process.env.OUTPUT_DIR || fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'chat-overflow-proof-'));
const phase = process.env.PHASE || 'after';
const token = 'segment'.repeat(100);
const text = `## Layout probe\n\n[https://example.com/${token}](https://example.com/${token})\n\nInline code: \`${token}\`\n\n\`\`\`text\n${token}\n\`\`\`\n\n| Value | Details |\n| --- | --- |\n| ${token} | ${token} |`;
const entries = [{role:'user',text}, {role:'assistant',text}, {role:'tool',tool:'exec_command',text:token}, {role:'result',text:token}];
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined, args: ['--no-sandbox'], ignoreDefaultArgs: ['--hide-scrollbars'] });
  const results = [], errors = [];
  try {
    const page = await browser.newPage();
    page.on('pageerror', error => errors.push(error.message));
    // Exercise the production components and renderer; only conversation transport is synthetic.
    const session = {id:'browser-test',title:'Conversation layout',agent:'Codex',phase:'Running',mode:'Interactive',uiMode:'terminal',accessRole:'Owner',browser:{phase:'Stopped'}};
    await page.routeWebSocket('**/ws/**', () => {});
    await page.route('**/api/**', route => {
      const url = new URL(route.request().url()), p = url.pathname;
      let json = [];
      if (p === '/api/config') json = {authority:''};
      else if (p === '/api/sessions') json = [session];
      else if (p === '/api/shared/view-token/session') json = {...session,accessRole:'Viewer',sharedBy:'owner@example.com'};
      else if (p.endsWith('/conversation')) json = {source:'native',offset:0,nextOffset:entries.length,length:entries.length,entries};
      else if (p.endsWith('/presentation')) json = {revision:0,fileId:null};
      else if (p.endsWith('/capabilities')) json = {canUpload:false,canRead:true};
      else if (p === '/api/agents/allowed') json = {agents:['Codex']};
      else if (p === '/api/admin/access') json = {isAdmin:false};
      else if (p === '/api/credentials' || p === '/api/credentials/accounts') json = {};
      return route.fulfill({json});
    });
    for (const width of [1280, 1440, 1920, 390]) {
      for (const [view, url] of [['workspace','/s/browser-test'], ['shared','/shared/view-token']]) {
        await page.setViewportSize({width,height:900});
        await page.goto(origin + url);
        await page.locator('[data-workspace-role="assistant"] .md-table').waitFor();
        await page.evaluate(() => document.fonts.ready);
        await page.locator('[data-work-log]').first().locator('summary').click();
        const result = await page.evaluate(() => {
          const size = el => ({client:el.clientWidth, scroll:el.scrollWidth});
          const conversation = document.querySelector('.conversation-scroll');
          return {document:size(document.documentElement),conversation:size(conversation),
            local:[...document.querySelectorAll('.md-code,.md-table')].map(el => ({kind:el.className,...size(el),overflow:getComputedStyle(el).overflowX})),
            overflowing:[...document.querySelectorAll('.timeline *')].filter(el => el.clientWidth && el.scrollWidth > el.clientWidth + 1).map(el => ({tag:el.tagName,cls:el.className,...size(el)}))};
        });
        results.push({width,view,...result});
        await page.screenshot({path:path.join(output,`${phase}-${view}-${width}.png`)});
      }
    }
    fs.writeFileSync(path.join(output,`${phase}.json`),JSON.stringify(results,null,2));
    console.table(results.map(({width,view,document,conversation}) => ({width,view,document:`${document.scroll}/${document.client}`,conversation:`${conversation.scroll}/${conversation.client}`})));
    if (phase !== 'before') for (const row of results) {
      assert(row.document.scroll <= row.document.client, `${row.view} ${row.width}: document overflow`);
      assert(row.conversation.scroll <= row.conversation.client, `${row.view} ${row.width}: conversation overflow`);
      assert(row.local.length === 4, 'Both messages render code and tables');
      for (const el of row.local) {
        assert.equal(el.overflow,'auto');
        assert(el.scroll > el.client, `${el.kind} keeps a local horizontal scroller`);
      }
    }
    assert.deepEqual(errors,[]);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
