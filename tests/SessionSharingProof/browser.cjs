const assert = require('node:assert/strict');
const { chromium } = require(process.env.PW_MODULE || 'playwright-core');
const ui = process.env.UI;
const backend = 'http://127.0.0.1:8080';
let passed = 0;
function check(value, label) { assert.ok(value, label); console.log('PASS', ++passed, label); }
async function api(path, user='alice', method='GET', body) {
 return fetch(backend + '/api' + path, { method, headers: { Cookie: `proof-user=${user}`, 'Content-Type':'application/json' }, body: body && JSON.stringify(body) });
}
(async () => {
 const browser = await chromium.connectOverCDP(process.env.CDP);
 const page = browser.contexts()[0].pages()[0];
 await page.setViewportSize({width:1440,height:1000});
 // The visible browser is in another pod. Forward its loopback origin to this host so
 // Chromium has a secure context for the actual PKCE WebCrypto implementation.
 if (process.env.RELAY) await page.route(ui + '/**', async route => {
   const request = route.request();
   const response = await fetch(request.url().replace(ui, process.env.RELAY), {
     method: request.method(), headers: request.headers(), body: request.postDataBuffer() || undefined, redirect: 'manual'
   });
   const headers = Object.fromEntries(response.headers);
   delete headers['content-encoding']; delete headers['content-length'];
   await route.fulfill({ status: response.status, headers, body: Buffer.from(await response.arrayBuffer()) });
 });
 await page.goto(ui);
 await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
 await page.context().clearCookies();
 await api('/ee/sessions/alice-session/shares/users/bob','alice','DELETE');
 await page.goto(ui + '/s/alice-session');
 await page.getByRole('button',{name:'Sign in',exact:true}).click();
 await page.getByRole('link',{name:'Sign in as alice',exact:true}).click();
 await page.getByRole('button',{name:'↗ Share',exact:true}).waitFor();
 const wrongUserStatus = await page.evaluate(async () => {
   const { api } = await import('/src/api.js');
   try { await api.getSession('bob-session'); return 200 } catch (error) { return error.status }
 });
 check(wrongUserStatus === 404, 'Alice JWT cannot read Bob’s foreign session');
 await page.goto(ui + '/s/bob-session');
 await page.getByText('This session is unavailable or is not shared with this account.',{exact:false}).waitFor();
 check(await page.getByText('This session is unavailable or is not shared with this account.',{exact:false}).isVisible(), 'wrong signed-in account gets clear access message');
 await page.goto(ui + '/s/alice-session');
 await page.getByRole('button',{name:'↗ Share',exact:true}).click();
 await page.getByRole('textbox',{name:'Recipient username or email'}).fill('bo');
 const choice = page.getByRole('button',{name:'Bob · Username: bob',exact:true});
 await choice.waitFor();
 check(await choice.isVisible(), 'owner sees bounded autocomplete with expected username');
 await page.screenshot({path:__dirname+'/picker.png'});
 await choice.click();
 await page.getByRole('button',{name:'Add',exact:true}).click();
 await page.locator('.person .pname').filter({hasText:'bob'}).waitFor();
 check(await page.locator('.person .pname').filter({hasText:'bob'}).isVisible(), 'owner shares using selected username');
 await page.getByRole('button',{name:'Create link',exact:true}).click();
 await page.locator('.one-time code').waitFor();
 check((await page.locator('.one-time code').innerText()).startsWith(ui + '/shared/'), 'copied relative backend URL becomes absolute');
 await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
 await page.context().clearCookies();
 await page.goto(ui + '/s/alice-session?proof=return');
 await page.getByRole('button',{name:'Sign in',exact:true}).click();
 await page.getByRole('link',{name:'Sign in as bob',exact:true}).click();
 await page.getByText('Sharing proof',{exact:true}).first().waitFor();
 check(new URL(page.url()).pathname === '/s/alice-session', 'OIDC callback returns to named session');
 check(new URL(page.url()).search === '?proof=return', 'login preserves query');
 await page.locator('[data-toggle-group=shared]').waitFor();
 check(await page.locator('[data-toggle-group=shared]').isVisible(), 'named grant is discoverable in sidebar');
 check(await page.getByText('Shared proof transcript',{exact:false}).first().isVisible(), 'recipient reads real stored transcript');
 check(!(await page.getByRole('button',{name:'Resume',exact:true}).count()), 'viewer cannot resume owner session');
 await page.screenshot({path:__dirname+'/recipient.png'});
 const json = await (await api('/sessions/alice-session','bob')).json();
 check(json.accessRole === 'Viewer' && !json.canManage && !json.canWrite && !('callbackToken' in json), 'session HTTP response is sanitized');
 check((await api('/sessions/alice-session', 'unknown')).status === 404, 'wrong user cannot read session');
 check((await api('/sessions/alice-session/conversation', 'unknown')).status === 404, 'wrong user cannot read conversation');
 check((await api('/sessions/alice-session/transcript', 'unknown')).status === 404, 'wrong user cannot read transcript');
 check((await api('/ee/sessions/bob-session/shares/recipients?query=al')).status === 404, 'foreign session cannot search recipients');
 check((await api('/ee/sessions/bob-session/shares/links','alice','POST',{role:'Viewer'})).status === 404, 'foreign session cannot mint links');
 const people = await (await api('/ee/sessions/alice-session/shares/recipients?query=bo')).json();
 check(people.length === 1 && people[0].username === 'bob' && !('email' in people[0]), 'bounded prefix picker suggests username without email');
 check((await (await api('/ee/sessions/alice-session/shares/recipients?query=')).json()).length === 0, 'empty query never enumerates');
 check((await (await api('/ee/sessions/alice-session/shares/recipients?query=ali')).json()).length === 0, 'self excluded');
 check((await (await api('/ee/sessions/alice-session/shares/recipients?query=bob%40example.com')).json())[0].username === 'bob', 'exact email maps to username');
 await api('/ee/sessions/alice-session/shares/users/bob','alice','DELETE');
 check((await api('/sessions/alice-session','bob')).status === 404, 'revoked named grant is denied immediately');
 await page.getByText('This session is unavailable or is not shared with this account.',{exact:false}).waitFor();
 check(await page.getByText('This session is unavailable or is not shared with this account.',{exact:false}).isVisible(), 'revocation removes active session and explains account/access');
 await page.screenshot({path:__dirname+'/revoked-user.png'});
 const link = await (await api('/ee/sessions/alice-session/shares/links','alice','POST',{role:'Viewer'})).json();
 await page.context().clearCookies();
 await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
 await page.goto(ui + link.url);
 await page.getByText('Sharing proof',{exact:true}).waitFor();
 check(await page.getByText('Shared session',{exact:true}).isVisible(), 'bearer link opens without login');
 await api('/ee/sessions/alice-session/shares/links/'+link.link.id,'alice','DELETE');
 await page.getByText('This share link is invalid, expired, or revoked.',{exact:false}).waitFor();
 check(await page.getByText('This share link is invalid, expired, or revoked.',{exact:false}).isVisible(), 'revoked bearer link has clear message');
 await page.goto(ui + '/shared/invalid');
 await page.getByText('This share link is invalid, expired, or revoked.',{exact:false}).waitFor();
 check(await page.getByText('This share link is invalid, expired, or revoked.',{exact:false}).isVisible(), 'invalid bearer link has clear message');
 await page.screenshot({path:__dirname+'/invalid-link.png'});
 await page.unrouteAll({behavior:'wait'});
 await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
