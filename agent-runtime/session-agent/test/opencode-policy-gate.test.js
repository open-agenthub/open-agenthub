'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const gateModule = import('../../opencode/policy-gate.mjs');
const pluginModule = import('../../opencode/policy-plugin.mjs');

const ENV = {
  AGENTHUB_MODE: 'autonomous', AGENTHUB_CALLBACK_URL: 'http://hub.example.com/internal/sessions/s1',
  AGENTHUB_CALLBACK_TOKEN: 'tok', AGENTHUB_APPROVAL_INTERVAL_MS: '10', AGENTHUB_APPROVAL_POLLS: '3'
};

function hub(routes) {
  const calls = [];
  const fetchImpl = async (url, init = {}) => {
    const pathname = new URL(url).pathname.replace('/internal/sessions/s1', '');
    const body = init.body ? JSON.parse(init.body) : undefined;
    calls.push({ method: init.method, pathname, body });
    const handler = routes[pathname];
    if (!handler) return { ok: false, text: async () => '{}' };
    const value = typeof handler === 'function' ? handler(body) : handler;
    return { ok: true, text: async () => JSON.stringify(value) };
  };
  return { calls, fetchImpl };
}

async function gate(routes, env = ENV, servers = []) {
  const { createGate } = await gateModule;
  const h = hub(routes);
  return { h, run: createGate({ env, fetchImpl: h.fetchImpl, sleep: async () => {}, servers }) };
}

test('the plugin module exports nothing but the plugin', async () => {
  // OpenCode calls every exported function as a plugin at startup.
  assert.deepEqual(Object.keys(await pluginModule), ['AgentHubPolicy']);
});

test('built-in tools are named as the hub policy names them', async () => {
  const { hubTool } = await gateModule;
  assert.deepEqual(hubTool('bash', { command: 'git status', description: 'x' }),
    { name: 'Bash', input: { command: 'git status' } });
  assert.deepEqual(hubTool('apply_patch', { patchText: '…' }), { name: 'Edit', input: {} });
  assert.deepEqual(hubTool('read', {}), { name: 'Read', input: {} });
});

test('MCP tools map to mcp__server__tool, longest server name first', async () => {
  const { hubTool, mcpServerNames } = await gateModule;
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'opencode-gate-'));
  const config = path.join(dir, 'mcp.json');
  fs.writeFileSync(config, JSON.stringify({ mcpServers: { 'my-srv': {}, my: {}, 'bad name': {} } }));
  const servers = mcpServerNames({ AGENTHUB_MCP_CONFIG: config });
  assert.deepEqual(servers, ['my-srv', 'my']);
  assert.equal(hubTool('my-srv_say_hi', {}, servers).name, 'mcp__my-srv__say_hi');
  assert.equal(hubTool('my_tool', {}, servers).name, 'mcp__my__tool');
  assert.equal(hubTool('unknown', {}, servers).name, 'unknown');
  assert.deepEqual(mcpServerNames({}), []);
});

test('an allowed call runs after a single policy check', async () => {
  const { h, run } = await gate({ '/agent-policy': { decision: 'allow' } });
  await run('bash', { command: 'git status' });
  assert.deepEqual(h.calls, [{ method: 'POST', pathname: '/agent-policy',
    body: { tool: 'Bash', input: { command: 'git status' } } }]);
});

test('a denied call throws the reason the model sees', async () => {
  const { run } = await gate({ '/agent-policy': { decision: 'deny' } });
  await assert.rejects(run('bash', { command: 'rm -rf /' }), /Blocked by the session policy/);
});

test('"ask" goes to the permission endpoint and an approval is rechecked', async () => {
  let policyCalls = 0;
  const { h, run } = await gate({
    '/agent-policy': () => ({ decision: policyCalls++ === 0 ? 'ask' : 'allow' }),
    '/permission': { decision: 'allow' }
  });
  await run('edit', { filePath: 'a' });
  assert.deepEqual(h.calls.map(call => call.pathname), ['/agent-policy', '/permission', '/agent-policy']);
  assert.deepEqual(h.calls[1].body, { tool: 'Edit', input: 'File change request' });
});

test('an approval loses to a sharing policy that now denies', async () => {
  let policyCalls = 0;
  const { run } = await gate({
    '/agent-policy': () => ({ decision: policyCalls++ === 0 ? 'ask' : 'deny' }),
    '/permission': { decision: 'allow' }
  });
  await assert.rejects(run('my_tool', {}), /not approved/);
});

test('a pending request is polled until someone answers', async () => {
  let polls = 0;
  const { run } = await gate({
    '/agent-policy': { decision: 'ask' },
    '/permission': { id: 'req1' },
    '/permission/req1': () => ({ decision: ++polls < 2 ? 'pending' : 'allowAlways' })
  });
  await run('bash', { command: 'npm test' });
  assert.equal(polls, 2);
});

test('an unanswered request expires and the call is denied', async () => {
  const { h, run } = await gate({
    '/agent-policy': { decision: 'ask' },
    '/permission': { id: 'req1' },
    '/permission/req1': { decision: 'pending' },
    '/permission/req1/expire': { decision: 'expired' }
  });
  await assert.rejects(run('bash', { command: 'npm test' }), /not approved/);
  assert.equal(h.calls.filter(call => call.pathname === '/permission/req1').length, 3);
  assert.equal(h.calls.at(-1).pathname, '/permission/req1/expire');
});

test('an unreachable hub fails closed in every mode', async () => {
  for (const mode of ['interactive', 'autonomous']) {
    const { run } = await gate({}, { ...ENV, AGENTHUB_MODE: mode });
    await assert.rejects(run('bash', { command: 'ls' }), /could not be reached/);
  }
  const { run } = await gate({ '/agent-policy': { decision: 'allow' } },
    { ...ENV, AGENTHUB_CALLBACK_URL: '' });
  await assert.rejects(run('read', {}), /could not be reached/);
});

test('bookkeeping tools are not put in front of an approver', async () => {
  const { h, run } = await gate({});
  await run('todowrite', { todos: [] });
  await run('question', {});
  assert.equal(h.calls.length, 0);
});
