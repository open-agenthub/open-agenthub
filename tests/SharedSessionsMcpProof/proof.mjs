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
const base = process.env.PROOF_URL ?? 'http://127.0.0.1:8408';
const root = path.resolve(import.meta.dirname, '../..');
// Match the runtime image's sibling layout without changing the checkout's dependency paths.
const runtime = fs.mkdtempSync(path.join(os.tmpdir(), 'shared-sessions-proof-'));
fs.cpSync(path.join(root, 'agent-runtime/sessions'), path.join(runtime, 'sessions'), { recursive: true });
fs.symlinkSync(path.resolve(process.env.RUNTIME ?? '/opt/session-agent', 'node_modules'), path.join(runtime, 'node_modules'));
async function connect(id) {
  const transport = new StdioClientTransport({ command: 'node', args: [path.join(runtime, 'sessions/server.mjs')],
    env: { ...process.env, AGENTHUB_SESSION_ID: id, AGENTHUB_CALLBACK_TOKEN: `proof-${id}`,
      AGENTHUB_CALLBACK_URL: `${base}/internal/sessions/${id}` }, stderr: 'inherit' });
  const client = new Client({name:'shared-session-proof',version:'1'});
  await client.connect(transport);
  return client;
}
const a = await connect('a');
const b = await connect('b');
const child = process.env.BASELINE === '1' ? null : await connect('b-child');
const call = async (client, name, args = {}) => {
  const response = await client.callTool({ name, arguments: args });
  return { error: response.isError === true, value: JSON.parse(response.content[0].text) };
};
const role = async value => assert.equal((await fetch(`${base}/__role/${value}`,{method:'POST'})).status,200);
try {
  if (process.env.BASELINE !== '1') {
    await role('none');
    const listed = (await call(a, 'agents_list')).value.filter(s => !s.self);
    assert.deepEqual(listed.map(s => s.id).sort(), ['child', 'parent', 'peer', 'sibling']);
    for (const target of listed) {
      assert.equal(target.owner, 'alice');
      assert.equal((await call(a, 'agent_send', { to: target.id, message: `fleet-${target.id}` })).error, false, `send ${target.id}`);
      assert.equal((await call(a, 'session_get', { id: target.id })).error, false, `read ${target.id}`);
      const recipient = await connect(target.id);
      try {
        assert((await call(recipient, 'agent_inbox')).value.messages.some(m => m.body === `fleet-${target.id}`));
      } finally { await recipient.close(); }
    }
    for (const id of ['outside', 'missing']) {
      assert.equal((await call(a, 'agent_send', { to: id, message: 'denied' })).error, true);
      assert.equal((await call(a, 'session_get', { id })).error, true);
    }
    console.log('PASS every listed owned peer/parent/child/sibling is sendable/readable; outside and missing denied; real inbox delivery');
  }
  await role('Viewer');
  if (process.env.BASELINE === '1') {
    assert(!(await call(a,'agents_list')).value.some(s=>s.id==='b'));
    assert.equal((await call(a,'session_get',{id:'b'})).error,true);
    await role('Collaborator');
    assert.equal((await call(a,'agent_send',{to:'b',message:'baseline'})).error,true);
    console.log('PASS baseline reproduces missing shared directory/status/message access');
    process.exitCode = 0;
  } else {
  const listed = await call(a,'agents_list');
  assert(listed.value.some(s=>s.id==='b'));
  const read = await call(a,'session_get',{id:'b'});
  assert.equal(read.error,false);
  assert.equal(read.value.owner,'bob');
  assert.equal(read.value.accessRole,'Viewer');
  assert.equal(read.value.mcpConfigJson,undefined);
  const waited = await call(a,'session_wait',{id:'b',timeoutMs:1,intervalMs:1});
  assert.equal(waited.error,false);
  assert.equal(waited.value.id,'b');
  assert.equal(waited.value.phase,'Paused');
  assert.equal((await call(a,'agent_send',{to:'b',message:'viewer denied'})).error,true);
  for (const [name,args] of [['session_delete',{id:'b'}],['session_convert',{sessionId:'b'}],['account_status',{sessionId:'b'}]])
    assert.equal((await call(a,name,args)).error,true);
  if (child) {
    assert(listed.value.some(s=>s.id==='b-child'));
    const status = await call(a,'session_get',{id:'b-child'});
    assert.equal(status.error,false);
    assert.equal(status.value.accessRole,'Viewer');
    assert.equal((await call(a,'agent_send',{to:'b-child',message:'viewer denied'})).error,true);
  }
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
  if (child) {
    assert.equal((await call(a,'agent_send',{to:'b-child',message})).error,false);
    const childInbox = await call(child,'agent_inbox');
    assert(childInbox.value.messages.some(m=>m.body===message && m.fromOwner==='alice'));
    console.log('PASS inherited child directory/read/viewer denial/collaborator message');
  }
  await role('none');
  assert(!(await call(a,'agents_list')).value.some(s=>s.id==='b'));
  assert.equal((await call(a,'session_get',{id:'b'})).error,true);
  assert.equal((await call(a,'agent_send',{to:'b',message:'revoked'})).error,true);
  if (child) {
    assert(!(await call(a,'agents_list')).value.some(s=>s.id==='b-child'));
    assert.equal((await call(a,'session_get',{id:'b-child'})).error,true);
    assert.equal((await call(a,'agent_send',{to:'b-child',message:'revoked'})).error,true);
  }
  console.log('PASS revoked share immediately denies list/read/send');
  await role('link');
  assert(!(await call(a,'agents_list')).value.some(s=>s.id==='b'));
  assert.equal((await call(a,'session_get',{id:'b'})).error,true);
  assert.equal((await call(a,'agent_send',{to:'b',message:'link'})).error,true);
  if (child) {
    assert(!(await call(a,'agents_list')).value.some(s=>s.id==='b-child'));
    assert.equal((await call(a,'session_get',{id:'b-child'})).error,true);
    assert.equal((await call(a,'agent_send',{to:'b-child',message:'link'})).error,true);
  }
  console.log('PASS bearer link grants no MCP access');
  }
} finally { await a.close(); await b.close(); await child?.close(); fs.rmSync(runtime, { recursive: true, force: true }); }
