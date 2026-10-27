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

 const expected=await fetch('http://127.0.0.1:17100/proof',{method:'POST'}).then(r=>r.json());
 await open('claude');
 const composer=page.locator('[data-workspace-composer] textarea');
 await page.locator('[data-drawer-agent]').click();
 await page.waitForFunction(()=>document.querySelector('.xterm-rows')?.textContent.includes('auto mode on'),null,{timeout:20000});
 await page.locator('[data-drawer-agent]').click();
 await composer.fill('ASK-ME about proof failures');await composer.press('Enter');
 const notice=page.locator('[data-cli-menu]');await notice.waitFor({timeout:30000});
 const shown=await notice.locator('[data-cli-title]').innerText();
 assert.equal(shown.replace(/\s+/g,' ').trim().replace(/^· /,''),expected.question);
 const field=notice.locator('[data-cli-free-text] textarea');
 await field.fill('Runtime first\nthen browser');await field.press('Enter');
 let answered=false;const end=Date.now()+20000;
 while(!answered&&Date.now()<end){answered=(await fetch('http://127.0.0.1:17100/proof').then(r=>r.json())).answered;await page.waitForTimeout(200);}
 assert.equal(answered,true);assert.deepEqual(errors,[]);
 console.log('PASS real Chromium: complete wrapped native question, free-text answer with newline, actual CLI tool result');
 await context.close();await browser.close();
})().catch(()=>{console.error('Question browser proof failed');process.exitCode=1;});
