const assert = require('node:assert/strict');
const { chromium } = require(process.env.PW_MODULE || 'playwright-core');
(async () => {
 const api = async (path, user, method='GET', body) => fetch('http://127.0.0.1:8080/api'+path, {method,headers:{Cookie:`proof-user=${user}`,'Content-Type':'application/json'},body:body&&JSON.stringify(body)});
 assert.equal((await api('/ee/sessions/alice-session/shares/users','alice','POST',{recipient:'bob',role:'Viewer'})).status,200);
 for (const suffix of ['', '/conversation', '/transcript']) {
  const response = await api('/sessions/alice-session'+suffix,'bob');
  assert.equal(response.status,404);
  console.log('BASELINE named recipient GET /api/sessions/<id>'+suffix+': 404');
 }
 const grants = await (await api('/sessions/shared','bob')).json();
 assert.equal(grants[0].id,'alice-session');
 console.log('BASELINE /api/sessions/shared contains grant, but frontend never requests it');
 const browser = await chromium.connectOverCDP(process.env.CDP);
 const page = browser.contexts()[0].pages()[0];
 await page.context().clearCookies();
 await page.context().addCookies([{name:'proof-user',value:'bob',url:process.env.UI}]);
 await page.goto(process.env.UI+'/s/alice-session');
 await page.getByText('Foreign session',{exact:true}).first().waitFor();
 assert.equal(await page.getByText('Shared with me',{exact:true}).count(),0);
 assert.equal(await page.locator('.term-bar .title').count(),0);
 console.log('BASELINE Chromium: named deep link falls through to home; no Shared with me group');
 await page.screenshot({path:__dirname+'/named-baseline.png'});
 await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
