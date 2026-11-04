import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
const require = createRequire(path.resolve(process.env.RUNTIME ?? '/opt/session-agent', 'package.json'));
const sdk = name => import(pathToFileURL(require.resolve(`@modelcontextprotocol/sdk/${name}`)).href);
const { Client } = await sdk('client/index.js');
const { StdioClientTransport } = await sdk('client/stdio.js');
const root = path.resolve(import.meta.dirname, '../..');
async function connect(id) {
  const transport = new StdioClientTransport({ command: 'node', args: [path.join(root, 'agent-runtime/sessions/server.mjs')],
    env: { ...process.env, AGENTHUB_SESSION_ID: id, AGENTHUB_CALLBACK_TOKEN: `proof-${id}`,
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:8408/internal/sessions/${id}` }, stderr: 'inherit' });
  const client = new Client({name:'shared-session-proof',version:'1'});
  await client.connect(transport);
  return client;
}
const a = await connect('a');
const b = await connect('b');
const call = async (client, name, args = {}) => {
  const response = await client.callTool({ name, arguments: args });
  return { error: response.isError === true, value: JSON.parse(response.content[0].text) };
};
const role = async value => assert.equal((await fetch(`http://127.0.0.1:8408/__role/${value}`,{method:'POST'})).status,200);
try {
  await role('Viewer');
  const listed = await call(a,'agents_list');
  assert(listed.value.some(s=>s.id==='b'));
  const read = await call(a,'session_get',{id:'b'});
  assert.equal(read.error,false);
  assert.equal(read.value.owner,'bob');
  assert.equal(read.value.accessRole,'Viewer');
  assert.equal(read.value.mcpConfigJson,undefined);
  assert.equal((await call(a,'agent_send',{to:'b',message:'viewer denied'})).error,true);
  for (const [name,args] of [['session_delete',{id:'b'}],['session_convert',{sessionId:'b'}],['account_status',{sessionId:'b'}]])
    assert.equal((await call(a,name,args)).error,true);
  console.log('PASS viewer list/read; send and management denied');
  await role('Collaborator');
  for(const flag of ['priority','interrupt']) assert.equal((await call(a,'agent_send',{to:'b',message:'flag denied',[flag]:true})).error,true);
  const message = `two-user-proof-${Date.now()}`;
  const sent = await call(a,'agent_send',{to:'Agent B',message});
  assert.equal(sent.error,false);
  assert.equal(sent.value.deliveredVia,'inbox');
  const inbox = await call(b,'agent_inbox');
  const received = inbox.value.messages.find(m=>m.body===message);
  assert(received);
  assert.equal(received.fromOwner,'alice');
  assert.equal(received.fromTitle,'Agent A (owner: alice)');
  assert.equal(received.from,'a');
  console.log('PASS collaborator A -> B real stdio/HTTP/Postgres inbox with sender owner/title');
  await role('none');
  assert(!(await call(a,'agents_list')).value.some(s=>s.id==='b'));
  assert.equal((await call(a,'session_get',{id:'b'})).error,true);
  assert.equal((await call(a,'agent_send',{to:'b',message:'revoked'})).error,true);
  console.log('PASS revoked share immediately denies list/read/send');
  await role('link');
  assert(!(await call(a,'agents_list')).value.some(s=>s.id==='b'));
  assert.equal((await call(a,'session_get',{id:'b'})).error,true);
  assert.equal((await call(a,'agent_send',{to:'b',message:'link'})).error,true);
  console.log('PASS bearer link grants no MCP access');
} finally { await a.close(); await b.close(); }
