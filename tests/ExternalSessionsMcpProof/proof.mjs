import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import os from 'node:os';
import fs from 'node:fs';
const require = createRequire(path.resolve(process.env.RUNTIME ?? '/opt/session-agent', 'package.json'));
const sdk = name => import(pathToFileURL(require.resolve(`@modelcontextprotocol/sdk/${name}`)).href);
const { Client } = await sdk('client/index.js');
const { StdioClientTransport } = await sdk('client/stdio.js');
const { StreamableHTTPClientTransport } = await sdk('client/streamableHttp.js');
const base = process.env.PROOF_URL ?? 'http://127.0.0.1:8410';
const root = path.resolve(import.meta.dirname, '../..');
const runtime = fs.mkdtempSync(path.join(os.tmpdir(), 'external-sessions-proof-'));
fs.cpSync(path.join(root, 'mcp/agenthub'), path.join(runtime, 'agenthub'), { recursive: true });
fs.symlinkSync(path.resolve(process.env.RUNTIME ?? '/opt/session-agent', 'node_modules'), path.join(runtime, 'node_modules'));
const clients = [];
const stdio = new Client({name:'external-stdio-proof',version:'1'});
await stdio.connect(new StdioClientTransport({command:'node',args:[path.join(runtime,'agenthub/server.mjs')],
  env:{...process.env,AGENTHUB_URL:base,AGENTHUB_TOKEN:'oah_external_proof_token'},stderr:'inherit'}));
clients.push(stdio);
const remote = new Client({name:'external-remote-proof',version:'1'});
await remote.connect(new StreamableHTTPClientTransport(new URL(`${base}/mcp`),
  {requestInit:{headers:{Authorization:'Bearer oah_external_proof_token'}}}));
clients.push(remote);
const call = async (client,name,args={}) => {
  const r = await client.callTool({name,arguments:args});
  let value;
  try { value = JSON.parse(r.content[0].text); } catch { value = r.content[0].text; }
  return {error:r.isError===true,value};
};
const role = async value => assert.equal((await fetch(`${base}/__role/${value}`,{method:'POST'})).status,200);
try {
  for (const [index, client] of clients.entries()) {
    await role('none');
    const owned = (await call(client,'agents_list')).value;
    assert.deepEqual(owned.map(s=>s.id).sort(),['a','child','outside','parent','peer','sibling']);
    for (const s of owned) {
      assert.equal(s.owner,'alice');
      assert.equal((await call(client,'session_get',{id:s.id})).error,false);
      assert.equal((await call(client,'agent_send',{to:s.id,message:`owned-${index}-${s.id}`})).error,false);
    }
    for (const grant of ['Viewer','Collaborator']) {
      await role(grant);
      for (const tool of ['session_list','agents_list']) {
        const listed = (await call(client,tool)).value;
        for (const id of ['b','b-child']) {
          const s = listed.find(s=>s.id===id);
          assert(s,`${tool} discovers ${id}`);
          assert.equal(s.owner,'bob'); assert.equal(s.accessRole,grant);
        }
      }
      for (const id of ['b','b-child']) {
        const status = await call(client,'session_get',{id});
        assert.equal(status.error,false); assert.equal(status.value.accessRole,grant);
        assert(!status.value.mcpConfigJson); assert(!status.value.prompt);
        const sent = await call(client,'agent_send',{to:id,message:`shared-${index}-${id}`});
        assert.equal(sent.error,grant==='Viewer');
        if (grant==='Collaborator') {
          assert.equal(sent.value.deliveredVia,'inbox');
          for (const flag of ['priority','interrupt'])
            assert.equal((await call(client,'agent_send',{to:id,message:'denied',[flag]:index===0?true:'true'})).error,true);
        }
      }
    }
    await role('none');
    assert(!(await call(client,'agents_list')).value.some(s=>s.id==='b'));
    for (const id of ['b','missing']) {
      assert.equal((await call(client,'session_get',{id})).error,true);
      assert.equal((await call(client,'agent_send',{to:id,message:'denied'})).error,true);
    }
    console.log(`PASS ${index===0?'personal-token stdio':'remote MCP'} owned fleet, named/inherited roles, sends, flags, revocation`);
  }
} finally { for (const c of clients) await c.close(); }
