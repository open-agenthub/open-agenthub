const { chromium } = require(process.env.PW_MODULE || 'playwright-core');
(async () => {
 const browser = await chromium.connectOverCDP(process.env.CDP);
 const page = browser.contexts()[0].pages()[0];
 const created = await fetch('http://127.0.0.1:8080/api/ee/sessions/alice-session/shares/links', {method:'POST',headers:{'Content-Type':'application/json',Cookie:'proof-user=alice'},body:JSON.stringify({role:'Viewer'})}).then(r=>r.json());
 const res = await page.goto(process.env.UI + created.url);
 await page.getByText('Shared session', {exact:true}).waitFor();
 await page.getByText('Sharing proof', {exact:true}).waitFor();
 console.log('Current main token link: document HTTP',res.status(),'; real shared endpoint and Chromium page succeed.');
 console.log('URL pathname format:', new URL(page.url()).pathname.replace(/[^/]+$/, '<token>'));
 await page.screenshot({path:__dirname+'/baseline.png'});
 await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
