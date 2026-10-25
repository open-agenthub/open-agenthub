import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const base = process.env.PROOF_BASE || 'http://127.0.0.1:8080';
const token = 'proof-token';
async function call(path, options = {}) {
  return fetch(base + path, { ...options, headers: { 'X-Agent-Token': token, 'Content-Type': 'application/json', ...options.headers } });
}
const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" onload="parent.svgExecuted=true;parent.localStorage.setItem(&quot;svg-proof&quot;,&quot;executed&quot;);parent.document.cookie=&quot;svg-proof=executed&quot;;alert(&quot;svg-proof&quot;)"><script>parent.svgExecuted=true;parent.localStorage.setItem('svg-proof','executed');parent.document.cookie='svg-proof=executed';alert('svg-proof')</script><rect width="40" height="40" fill="red"/><image href="http://127.0.0.1:8080/__svg-external"/></svg>`;
const cases = [ ['probe.svg', 'image/svg+xml', svg], ['probe.html', 'text/html', '<script>parent.htmlExecuted=true</script>'], ['archive.zip', 'application/zip', Buffer.from([0x50,0x4b,0x03,0x04,0,255])], ['LICENSE', 'application/octet-stream', 'arbitrary extensionless text'], ['empty.bin', 'application/octet-stream', ''] ];
const output = [];
for (const [name,mimeType,body] of cases) {
  const bytes = Buffer.from(body);
  const reserve = await call('/internal/sessions/s1/files/reserve', {method:'POST',body:JSON.stringify({name,mimeType,size:bytes.length})});
  assert.equal(reserve.status,200,`${name} reserve: ${await reserve.clone().text()}`);
  const {file,upload} = await reserve.json();
  const url = new URL(upload.url,base);
  const uploaded = await fetch(url,{method:'PUT',body:bytes,headers:{'Content-Type':mimeType,...upload.headers,...(upload.kind==='proxy'?{'X-Agent-Token':token}:{})}});
  assert.ok(uploaded.ok, `${name} upload ${uploaded.status}`);
  const completed = await call(`/internal/sessions/s1/files/${file.id}/complete`,{method:'POST'});
  assert.equal(completed.status,200,`${name} complete: ${await completed.clone().text()}`);
  const ready = await completed.json();
  assert.equal(ready.mimeType,name.endsWith('.svg')?'image/svg+xml':'application/octet-stream');
  const content = await call(`/internal/sessions/s1/files/${file.id}/content`);
  assert.equal(content.status,200);
  assert.equal(content.headers.get('x-content-type-options'),'nosniff');
  assert.equal(content.headers.get('content-security-policy'),"sandbox; default-src 'none'");
  assert.match(content.headers.get('content-disposition'),/^attachment/);
  assert.deepEqual(Buffer.from(await content.arrayBuffer()),bytes);
  const owner = await call(`/api/sessions/s1/files/${file.id}/content`);
  assert.equal(owner.status,200);
  assert.equal(owner.redirected,false);
  assert.match(owner.headers.get('content-disposition'),/^attachment/);
  assert.equal(owner.headers.get('content-security-policy'),"sandbox; default-src 'none'");
  const sibling = await fetch(`${base}/internal/sessions/s2/files/project/s1/${file.id}/content`,{headers:{'X-Agent-Token':'sibling-token'}});
  assert.equal(sibling.status,200);
  assert.deepEqual(Buffer.from(await sibling.arrayBuffer()),bytes);
  output.push(ready); console.log(`PASS ${name}: upload, complete, headers, byte-identical download and sibling download`);
}
for(const [name,mimeType,size] of [['large.svg','image/svg+xml',20*1024*1024+1],['large.bin','application/octet-stream',50*1024*1024+1]]) {
  const response = await call('/internal/sessions/s1/files/reserve',{method:'POST',body:JSON.stringify({name,mimeType,size})});
  assert.equal(response.status,413); console.log(`PASS ${name}: size limit retained`);
}
await fs.writeFile(new URL('./files.local.json',import.meta.url),JSON.stringify(output));
