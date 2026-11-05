// Render the real components with synthetic recovery states; HTTP/backend behavior is
// proved separately by Scenario.cs so this fixture can focus on visible recovery actions.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || '/opt/session-agent/node_modules/playwright-core');
const assert = require('node:assert/strict');
(async () => {
  const browser = await chromium.connectOverCDP(process.env.CDP_URL);
  const page = await browser.contexts()[0].newPage();
  const source = process.env.UI_URL;
  const origin = 'http://localhost:5173';
  const component = await (await fetch(source + '/src/components/McpServersPane.vue')).text();
  const vuePath = component.match(/from "([^"]*vue\.js[^"]*)"/)[1];
  // Bridge the remote browser's secure localhost origin to this isolated Vite server.
  // Fulfilling subresources avoids private-network access restrictions on pod IP HTTP.
  await page.route(origin + '/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/@vite/client') return route.fulfill({contentType:'application/javascript',body:`
      export function createHotContext(){return {accept(){},dispose(){},prune(){},on(){},data:{}}}
      export function updateStyle(id,content){let style=document.querySelector('style[data-proof-id="'+id+'"]');if(!style){style=document.createElement('style');style.dataset.proofId=id;document.head.append(style)}style.textContent=content}
      export function removeStyle(){}
    `});
    const response = await fetch(source + url.pathname + url.search);
    const headers = Object.fromEntries(response.headers);
    delete headers['content-encoding']; delete headers['content-length'];
    await route.fulfill({status:response.status,headers,body:Buffer.from(await response.arrayBuffer())});
  });
  const errors = [];
  page.on('pageerror', error => { errors.push(error.message); console.error('PAGE',error.message); });
  page.on('console', message => { if(message.type()==='error') console.error('CONSOLE',message.text()); });
  page.on('requestfailed', request=>console.error('REQUEST',request.url(),request.failure()));
  await page.route('**/api/**', route => {
    const pathname = new URL(route.request().url()).pathname;
    let json = [];
    if (pathname === '/api/mcp-servers') json = [{ id:'proof',name:'example-api',kind:'api',mine:true,owner:'admin',hasSecret:true,needsReentry:true,configJson:'{"specUrl":"https://example.com/spec.json"}' }];
    if (pathname === '/api/webhook-triggers') json = [{ id:'proof',name:'Example webhook',events:['opened'],agent:'Claude',needsReentry:true,url:'https://example.com/api/git/webhooks/proof' }];
    return route.fulfill({json});
  });
  await page.route(origin + '/proof', route => route.fulfill({contentType:'text/html',body:`<!doctype html><html><head><meta charset="utf-8"></head><body style="background:#16181d;color:#eee"><div id="proof"></div><script type="module">
    import {createApp,h} from '${vuePath}';
    import Mcp from '/src/components/McpServersPane.vue';
    import Webhooks from '/src/components/WebhooksPane.vue';
    createApp({render:()=>h('main',[h(Mcp,{isAdmin:true}),h(Webhooks)])}).mount('#proof');
  </script></body></html>`}));
  await page.setViewportSize({width:1280,height:1200});
  await page.goto(origin + '/proof');
  await page.locator('[data-mcp-needs-reentry]').waitFor();
  assert.match(await page.locator('[data-mcp-needs-reentry]').innerText(), /re-enter.*administrator/);
  assert.match(await page.locator('[data-webhook-needs-reentry]').innerText(), /Recreate.*administrator/);
  assert.equal(await page.locator('[data-mcp-has-secret]').count(),1);
  await page.locator('[data-mcp-edit]').click();
  assert.equal(await page.locator('[data-mcp-save]').count(),1);
  await page.screenshot({path:process.env.SCREENSHOT || '/tmp/data-protection-recovery.png',fullPage:true});
  assert.deepEqual(errors,[]);
  console.log('PASS: real Chromium renders catalog/webhook recovery hints, configured-secret badge and working Edit action; no page errors');
  await page.close();
  await browser.close();
})().catch(error=>{console.error(error);process.exitCode=1});
