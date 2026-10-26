// Fixture pages and a stand-in for the backend's /internal/sessions/{id}/browser route, shared
// by the proof scripts. The stand-in answers browser_start with whatever CDP endpoint the
// script points it at, so a script can move the "pod" to a new address mid-run.
import http from 'node:http';

const pages = {
  '/': `<title>home</title><h1>home</h1>
    <button id="nav" onclick="location.href='/slow'">navigate slowly</button>
    <button id="popup" onclick="window.open('/target?popup','oauth','popup,width=500,height=600')">popup</button>
    <button id="plain-open" onclick="window.open('/target?plain')">plain open</button>
    <a id="blank" href="/target?blank" target="_blank">blank link</a>
    <button id="close-self" onclick="window.close()">close self</button>
    <button id="replace" onclick="document.body.innerHTML='<p>gone</p>'">detach self</button>
    <button id="busy" onclick="const end=Date.now()+15000;while(Date.now()<end){}">busy</button>
    <button id="swap" onclick="location.href=location.href.replace('127.0.0.1','localhost')+'target?swap'">cross site</button>
    <button id="frames" onclick="for(let i=0;i<20;i++){const f=document.createElement('iframe');f.src=location.origin.replace('127.0.0.1','localhost')+'/target?f'+i;document.body.append(f)};setTimeout(()=>location.href='/target?after-frames',30)">frames</button>
    <a id="download" href="/download">download</a>
    <input id="file" type="file" aria-label="upload">
    <button id="alert" onclick="alert('hi')">alert</button>
    <button id="confirm-nav" onclick="onbeforeunload=e=>{e.preventDefault();e.returnValue='x'};document.body.append('dirty');setTimeout(()=>location.href='/target?after-unload',50)">leave dirty</button>`,
  '/target': '<title>target</title><h1>target</h1>',
  '/opener': `<title>opener</title><script>
    window.addEventListener('message', e => { document.title = 'got:' + e.data; });
    </script><button id="oauth" onclick="window.open('/oauth','oauth','popup,width=500,height=600')">sign in</button>`,
  '/oauth': `<title>oauth</title><script>
    window.opener && window.opener.postMessage('token-ok', '*'); setTimeout(() => window.close(), 200);
    </script>`
};

export function startFixtures(port = 0) {
  let cdpEndpoint = '';
  const calls = [];
  const server = http.createServer(async (request, response) => {
    const url = new URL(request.url, 'http://x');
    if (url.pathname === '/internal/sessions/s1/browser') {
      calls.push(request.method);
      if (request.method === 'DELETE') { response.writeHead(204); return response.end(); }
      response.writeHead(200, { 'Content-Type': 'application/json' });
      return response.end(JSON.stringify({
        browser: { phase: 'Running', screenWidth: 1440, screenHeight: 900 }, cdpEndpoint, podIp: '127.0.0.1'
      }));
    }
    if (url.pathname === '/download') {
      response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Disposition': 'attachment; filename=a.bin' });
      return response.end(Buffer.alloc(1024));
    }
    if (url.pathname === '/slow') {
      await new Promise(resolve => setTimeout(resolve, 15_000));
      response.writeHead(200, { 'Content-Type': 'text/html' });
      return response.end('<title>slow</title>slow');
    }
    const body = pages[url.pathname];
    response.writeHead(body ? 200 : 404, { 'Content-Type': 'text/html' });
    response.end(body ?? 'missing');
  });
  return new Promise(resolve => server.listen(port, '127.0.0.1', () => resolve({
    server,
    calls,
    base: `http://127.0.0.1:${server.address().port}`,
    callback: `http://127.0.0.1:${server.address().port}/internal/sessions/s1`,
    setCdp(value) { cdpEndpoint = value; }
  })));
}
