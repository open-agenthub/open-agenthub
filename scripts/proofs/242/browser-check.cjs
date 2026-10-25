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
 if (process.env.SIGNIN_CREDENTIAL_ONLY) {
  await open('opencode');
  await page.locator('[data-drawer-agent]').click();
  await page.waitForFunction(()=>document.querySelector('.xterm-rows')?.textContent.includes('Select provider'),null,{timeout:15000});
  await page.locator('.xterm-helper-textarea').first().press('Enter');
  await card.locator('[data-cli-signin-code]').waitFor({timeout:15000});
  console.log('PASS real credential selector -> credential screen through selected runtime source');
  assert.deepEqual(errors,[]);
  await context.close();await browser.close();return;
 }
 if (!process.env.SIGNIN_RECOVERY_ONLY) {
 await open('claude');
 console.log('STEP Claude page loaded');
 await composer.fill('/login');await composer.press('Enter');
 console.log('STEP /login submitted');
 await page.locator('[data-cli-option="1"]').click({timeout:15000});
 console.log('STEP login method selected');
 await card.waitFor({timeout:15000});
 const link=card.locator('[data-cli-signin-open]');
 await page.mouse.move(0,0);
 await page.screenshot({path:'/tmp/signin-242-desktop.png',mask:[page.locator('.xterm'),page.locator('[data-cli-device-code]')]});
 await page.setViewportSize({width:390,height:844});
 await page.waitForTimeout(1500);
 await card.locator('[data-cli-signin-open]').waitFor({timeout:10000});
 await page.screenshot({path:'/tmp/signin-242-mobile.png',mask:[page.locator('.xterm'),page.locator('[data-cli-device-code]')]});
 await page.setViewportSize({width:1280,height:900});
 assert.equal(await link.getAttribute('target'),'_blank');
 assert.match(await link.getAttribute('rel'),/noopener/);
 assert.match(await link.getAttribute('href'),/^https:\/\//);
 assert.equal(await page.locator('[data-drawer-agent]').getAttribute('aria-expanded'),'false');
 await card.locator('[data-cli-signin-code]').fill('example-authorization-code');
 await card.locator('[data-cli-signin-submit]').click();
 await page.getByText('Sign-in failed',{exact:true}).waitFor({timeout:15000});
 await page.getByRole('button',{name:'Retry sign-in',exact:true}).click();
 await card.locator('[data-cli-signin-code]').waitFor({timeout:15000});
 assert.equal(await card.locator('[data-cli-signin-code]').inputValue(),'');
 // Entering /login again after closing the screen must produce a fresh card.
 await card.getByRole('button',{name:'Cancel (Esc)',exact:true}).click();
 await card.waitFor({state:'detached',timeout:15000});
 await composer.fill('/login');await composer.press('Enter');
 await page.locator('[data-cli-option="1"]').click({timeout:15000});
 await card.locator('[data-cli-signin-code]').waitFor({timeout:15000});
 console.log('PASS Claude /login, pasted invalid code, failed outcome, retry, and repeated /login');
 // Wait across a real periodic upload and inspect only sanitized booleans.
 await page.waitForTimeout(32000);
 const stored=await fetch(hub+'/__proof/check').then(r=>r.json());
 assert.equal(stored.stateUploads,0);
 await page.locator('[data-state-withheld]').waitFor();
 await page.reload();
 await page.locator('[data-state-withheld]').waitFor({timeout:15000});
 console.log('PASS withheld-state notice survives reconnect without authorization data');
 assert.ok(stored.uploads>0);assert.equal(stored.authTextInUploads,false);assert.equal(stored.authTextInTranscript,false);
 console.log('PASS real periodic scrollback/transcript exports contain no auth material');
 if (!process.env.SIGNIN_CLAUDE_ONLY) {
 await open('codex');await card.waitFor({timeout:15000});
 const code=await card.locator('[data-cli-device-code]').innerText();
 await card.getByRole('button',{name:'Copy device code',exact:true}).click();
 const copied=await page.evaluate(()=>navigator.clipboard.readText());assert.equal(copied,code);
 assert.equal(await card.locator('[data-cli-signin-code]').count(),0);
 console.log('PASS Codex device sign-in and real secure-context clipboard');
 await open('cursor');await card.waitFor({timeout:15000});
 assert.equal(await card.locator('[data-cli-signin-code]').count(),0);
 assert.equal(await card.locator('[data-cli-signin-open]').count(),1);
 console.log('PASS Cursor browser sign-in, no paste box while polling');
 await open('opencode');
 // Navigate the real provider selector in its terminal, then verify native credential entry.
 await page.locator('[data-drawer-agent]').click();
 await page.waitForFunction(()=>document.querySelector('.xterm-rows')?.textContent.includes('Select provider'),null,{timeout:15000});
 await page.locator('.xterm-helper-textarea').first().press('Enter');
 await card.locator('[data-cli-signin-code]').waitFor({timeout:15000});
 console.log('PASS OpenCode default credential flow');
 await open('openclaw');
 await page.locator('[data-drawer-agent]').click();
 const terminal=page.locator('.xterm-helper-textarea').first();
 await page.waitForFunction(()=>document.querySelector('.xterm-rows')?.textContent.includes('Token provider'),null,{timeout:15000});
 await terminal.press('Enter');
 await page.waitForFunction(()=>document.querySelector('.xterm-rows')?.textContent.includes('Token method'),null,{timeout:15000});
 await terminal.press('Enter');
 await card.locator('[data-cli-signin-code]').waitFor({timeout:15000});
 console.log('PASS OpenClaw setup-token flow');
 }
 }
 await open('claude',true);
 console.log('STEP expired-login proof started');
 await page.waitForTimeout(2500);
 await composer.fill('/login');await composer.press('Enter');
 await page.locator('[data-cli-option="1"]').click({timeout:15000});
 await card.locator('[data-cli-signin-code]').waitFor({timeout:15000});
 await page.waitForTimeout(32000);
 await page.locator('[data-state-withheld]').waitFor({timeout:10000});
 const state=new URL(await card.locator('[data-cli-signin-open]').getAttribute('href')).searchParams.get('state');
 assert.ok(state);
 await card.locator('[data-cli-signin-code]').fill('example-code#'+state);
 await card.locator('[data-cli-signin-submit]').click();
 await page.getByText('Signed in',{exact:true}).waitFor({timeout:15000});
 await card.getByRole('button',{name:'Continue',exact:true}).click();
 await card.waitFor({state:'detached',timeout:15000});
 const mockCounts=await fetch('http://127.0.0.1:18443').then(r=>r.json());
 assert.ok(mockCounts.expired>0);
 console.log('PASS expired fake credentials -> /login -> real CLI success -> Continue in Chromium');
 await page.waitForTimeout(32000);
 const completed=await fetch(hub+'/__proof/check').then(r=>r.json());
 await page.locator('[data-state-withheld]').waitFor({state:'detached'});
 assert.ok(completed.privacy && completed.privacy.released > 0);
 assert.ok(completed.stateUploads>0);assert.equal(completed.authTextInUploads,false);assert.equal(completed.authTextInTranscript,false);
 console.log('PASS real tar/gzip state upload after sign-in contains no authorization data');
 assert.deepEqual(errors,[]);
 await context.close();await browser.close();
})().catch(async error=>{if(globalThis.proofPage){await globalThis.proofPage.screenshot({path:'/tmp/signin-242-debug.png',mask:[globalThis.proofPage.locator('.xterm'),globalThis.proofPage.locator('[data-cli-device-code]')]}).catch(()=>{});console.log('Safe UI state:',JSON.stringify({menus:await globalThis.proofPage.locator('[data-cli-menu]').count(),cards:await globalThis.proofPage.locator('[data-cli-signin]').count(),errors:await globalThis.proofPage.locator('[data-workspace-composer] [role=alert]').allTextContents()}));}console.error(error.name+': '+error.message.split('\n')[0]);process.exit(1)});
