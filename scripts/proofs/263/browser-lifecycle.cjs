// Real Chromium talks to the real CLIs through the branch's real common server.
// Assertions compare auth data in memory; logs and screenshots never contain it.
const assert=require('node:assert/strict');
const {chromium}=require('/opt/session-agent/node_modules/playwright-core');
const host=process.env.SIGNIN_UI_HOST||'127.0.0.1:5173';
const hub='http://127.0.0.1:8080';
(async()=>{
 const browser=await chromium.connectOverCDP(process.env.SIGNIN_CDP||'http://127.0.0.1:9222');
 const context=await browser.newContext({viewport:{width:1280,height:900}});
 // localhost is a secure context; forward both HTTP and WS to this proof's isolated frontend.
 await context.route('http://localhost:5173/**',async route=>{
  const response=await fetch(route.request().url().replace('localhost:5173',host));
  const headers=Object.fromEntries(response.headers);delete headers['content-encoding'];delete headers['content-length'];
  await route.fulfill({status:response.status,headers,body:Buffer.from(await response.arrayBuffer())});
 });
 await context.routeWebSocket('ws://localhost:5173/**', route => {
  if (!new URL(route.url()).pathname.startsWith('/ws/')) { route.onMessage(()=>{}); return; }
  const {WebSocket}=require('/opt/session-agent/node_modules/ws');
  const upstream=new WebSocket('ws://127.0.0.1:8080'+new URL(route.url()).pathname);
  const queued=[];
  route.onMessage(data=>{if(upstream.readyState===WebSocket.OPEN)upstream.send(data);else queued.push(data)});
  upstream.on('open',()=>{for(const data of queued)upstream.send(data)});
  upstream.on('message',data=>route.send(data.toString()));
  upstream.on('close',()=>route.close());upstream.on('error',()=>route.close());route.onClose(()=>upstream.close());
 });
 await context.grantPermissions(['clipboard-read','clipboard-write'],{origin:'http://localhost:5173'});
 const page=await context.newPage();
 globalThis.proofPage=page;
 const errors=[];page.on('pageerror',error=>{errors.push(error.message);console.log('Browser error category:',JSON.stringify({type:error.name,notDefined:/not defined/.test(error.message),notFunction:/not a function/.test(error.message),undefinedProperty:/undefined|null/.test(error.message),websocket:/WebSocket/i.test(error.message),aborted:/aborted|cancelled/i.test(error.message),stack:(error.stack||'').split('\n').slice(1,3).map(line=>line.replace(/https?:\/\/[^ )]+/g,'[source]'))}));});
 await page.addInitScript(()=>localStorage.setItem('agenthub.preferredUi','workspace'));
 async function open(agent,mock=false){
  await fetch(hub+'/__proof/agent',{method:'POST',body:JSON.stringify({agent,mock})});
  await page.goto('http://localhost:5173/s/signin-proof');
  await page.locator('[data-workspace-composer]').waitFor({timeout:30000});
 }
 const composer=page.locator('[data-workspace-composer] textarea');
 const card=page.locator('[data-cli-signin]');
 for(const agent of (process.env.SIGNIN_BROWSER_AGENT?[process.env.SIGNIN_BROWSER_AGENT]:['codex','opencode'])) {
  await open(agent,true);console.log('STEP '+agent+' opened');
  if(agent==='opencode') {
   await page.locator('[data-drawer-agent]').click();
   // The PTY harness selects the first real provider; credential submission stays in the UI.
   const input=card.locator('[data-cli-signin-code]');await input.waitFor({timeout:15000});
   assert.equal(await input.getAttribute('type'),'password');
   console.log('STEP credential input ready');await input.fill('example-263-test-credential');await card.locator('[data-cli-signin-submit]').click();
  }
  await page.locator('[data-drawer-agent]').evaluate(el=>{if(el.getAttribute('aria-expanded')==='false')el.click()});
  await page.waitForFunction(agent=>{const text=document.querySelector('.xterm-rows')?.textContent||'';return agent==='codex'?text.includes('Codex')&&text.includes('›'):text.includes('Ask anything')&&text.includes('commands')},agent,{timeout:30000});
  await card.waitFor({state:'detached',timeout:30000});
  await page.waitForTimeout(3000);
  assert.equal(await composer.inputValue(),'');
  await page.setViewportSize({width:390,height:844});await page.waitForTimeout(1500);
  assert.equal(await card.count(),0);
  await page.setViewportSize({width:1280,height:900});
  await page.waitForTimeout(32000);
  const stored=await fetch(hub+'/__proof/check').then(r=>r.json());
  assert.ok(stored.stateUploads>0);assert.equal(stored.authTextInUploads,false);assert.equal(stored.authTextInTranscript,false);
  assert.equal(await page.locator('[data-state-withheld]').count(),0);
  console.log('PASS '+agent+' actual login wrapper, completed card, empty composer, mobile resize and clean state upload');
 }
 assert.deepEqual(errors,[]);await context.close();await browser.close();
})().catch(async error=>{if(globalThis.proofPage)console.log(await globalThis.proofPage.evaluate(()=>{const t=document.querySelector('.xterm-rows')?.textContent||'';return {card:!!document.querySelector('[data-cli-signin]'),selector:t.includes('Select provider'),apiKey:/API key/.test(t),native:t.includes('Ask anything'),menu:!!document.querySelector('[data-cli-menu]')}}).catch(()=>({}))); console.error(error.name+': browser lifecycle assertion failed');console.error((error.stack||'').split('\n').filter(line=>/^\s+at /.test(line)).join('\n'));process.exit(1)});
