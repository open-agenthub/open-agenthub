'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createHarness, requestHttp, FakeSocket } = require('./common-server-harness');
const requestId = '11111111-1111-4111-8111-111111111111';
const nextId = '22222222-2222-4222-8222-222222222222';
const headers = { 'X-Agent-Token': 'correct-token' };
const env = { AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_STATE_PUT_URL: 'https://example.com/state' };
const route = (h, action, id = requestId, nativeId) => requestHttp(h, 'POST', '/agenthub/provider-switch', headers, JSON.stringify({ requestId: id, action, nativeId }));

test('required native resume refuses unrestored state before spawning and never falls back to fresh', () => {
  const strict = { AGENTHUB_REQUIRE_NATIVE_RESUME: '1', AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '0' };
  assert.throws(() => createHarness(strict), /saved conversation state was not restored/);
  const h = createHarness({ ...strict, AGENTHUB_STATE_RESTORED: '1' }, { isMissingResume: () => true });
  h.terminals[0].emitExit({ exitCode: 1 });
  assert.equal(h.spawns.length, 1);
});

test('an uncertain replacement native resume never falls back to a fresh target', () => {
  const h = createHarness({ ...env, AGENTHUB_PROVIDER_SWITCH_ID: requestId }, { isMissingResume: () => true });
  h.terminals[0].emitExit({ exitCode: 1 });
  assert.equal(h.spawns.length, 1);
});

test('reservation requires a supported driver, valid authority and a completed safe turn', async () => {
  const unsupported = createHarness(env);
  assert.equal((await requestHttp(unsupported, 'GET', '/agenthub/provider-switch')).status, 401);
  assert.equal((await requestHttp(unsupported, 'GET', '/agenthub/provider-switch', headers)).status, 400);
  const unknown = createHarness(env, { supportsProviderSwitch: true, turnHooks: true });
  assert.equal((await route(unknown, 'prepare')).status, 409);
  assert.equal((await requestHttp(unknown, 'POST', '/agenthub/provider-switch', headers, '{')).status, 400);
});

test('target holds user input until native evidence and archive checkpoint, but accepts terminal negotiation', async () => {
  const file = '/home/agent/.test-agent/conversation.jsonl';
  const prompt = 'Provider handover\nOmitted: old native state.\nTask: continue.';
  const files = { [file]: JSON.stringify({ type: 'user', message: { role: 'user', content: 'unrelated' } }) + '\n' };
  let clock = 10_000;
  const h = createHarness({ ...env, AGENTHUB_PROVIDER_SWITCH_ID: requestId, AGENTHUB_PROMPT: prompt },
    { supportsProviderSwitch: true, turnHooks: true, findTranscript: () => file, nativeIdentity: () => 'native-one' },
    { files, now: () => clock });
  const reservedShell = new FakeSocket(); h.runtime.webSocketServer.connect(reservedShell, '/shell');
  assert.deepEqual(reservedShell.closed, [1013]);
  const socket = new FakeSocket(); h.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', JSON.stringify({ type: 'resize', cols: 120, rows: 36 }));
  socket.emit('message', JSON.stringify({ type: 'input', data: '\x1b[1;1R' }));
  const before = h.terminals[0].writes.length;
  socket.emit('message', JSON.stringify({ type: 'input', data: 'must wait' }));
  assert.equal(h.terminals[0].writes.length, before);
  assert.equal((await route(h, 'release')).status, 409);
  assert.equal((await route(h, 'prepare', nextId)).status, 409);
  files[file] = JSON.stringify({ type: 'user', message: { role: 'user', content: prompt } }) + '\n';
  assert.equal((await route(h, 'release', requestId, 'wrong-native')).status, 409);
  assert.equal((await route(h, 'checkpoint', requestId, 'native-one')).status, 200);
  assert.equal((await route(h, 'release')).status, 200);
  socket.emit('message', JSON.stringify({ type: 'input', data: 'after handover' }));
  assert.equal(h.terminals[0].writes.at(-1), 'after handover');
  assert.equal((await route(h, 'release')).status, 200);
  clock = 11_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'prompt' }), '127.0.0.1');
  clock = 14_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'stop' }), '127.0.0.1');
  const competing = await Promise.all([route(h, 'prepare', nextId),
    route(h, 'prepare', '33333333-3333-4333-8333-333333333333')]);
  assert.deepEqual(competing.map(result => result.status).sort(), [200, 409]);
  const reserved = h.terminals[0].writes.length;
  // An old release retry must not reopen the terminal reserved by a later switch.
  assert.equal((await route(h, 'release')).status, 200);
  socket.emit('message', JSON.stringify({ type: 'input', data: 'still reserved' }));
  assert.equal(h.terminals[0].writes.length, reserved);
  assert.equal((await requestHttp(h, 'DELETE', '/agenthub/provider-switch', headers, JSON.stringify({ requestId: nextId }))).status, 200);
});

test('a target without a configured native archive cannot release its reservation', async () => {
  const file = '/home/agent/.test-agent/conversation.jsonl';
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_PROVIDER_SWITCH_ID: requestId, AGENTHUB_PROMPT: 'handover' },
    { supportsProviderSwitch: true, turnHooks: true, findTranscript: () => file, nativeIdentity: () => 'native-one' },
    { files: { [file]: JSON.stringify({ type: 'user', message: { role: 'user', content: 'handover' } }) + '\n' } });
  assert.equal((await route(h, 'release')).status, 409);
});

test('an open shell is a known workload that must close before source reservation', async () => {
  const file = '/home/agent/.test-agent/conversation.jsonl';
  let clock = 10_000;
  const h = createHarness(env, { supportsProviderSwitch: true, turnHooks: true, findTranscript: () => file, nativeIdentity: () => 'native-one' },
    { files: { [file]: '{"type":"user","message":{"role":"user","content":"source"}}\n' }, now: () => clock });
  const agent = new FakeSocket(); h.runtime.webSocketServer.connect(agent, '/');
  agent.emit('message', JSON.stringify({ type: 'resize', cols: 120, rows: 36 }));
  clock = 11_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'prompt' }), '127.0.0.1');
  clock = 14_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'stop' }), '127.0.0.1');
  const shell = new FakeSocket(); h.runtime.webSocketServer.connect(shell, '/shell');
  assert.equal((await route(h, 'prepare')).status, 409);
  shell.emit('close');
  assert.equal((await route(h, 'prepare')).status, 200);
});

test('native source quit is submitted once and failed final usage acknowledgment holds the stop', async () => {
  const file = '/home/agent/.test-agent/conversation.jsonl';
  let clock = 10_000, acceptUsage = false;
  const h = createHarness({ ...env, AGENTHUB_CALLBACK_URL: 'https://example.com/internal/session' },
    { supportsProviderSwitch: true, turnHooks: true, providerSwitchExitKeys: ['/exit', '\r'],
      findTranscript: () => file, nativeIdentity: () => 'source-native', readUsage: () => ({ inputTokens: 10 }) },
    { files: { [file]: '{"type":"user","message":{"role":"user","content":"source"}}\n' }, now: () => clock,
      fetchResponse: url => ({ ok: !url.endsWith('/usage') || acceptUsage, text: async () => '' }) });
  const socket = new FakeSocket(); h.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', JSON.stringify({ type: 'resize', cols: 120, rows: 36 }));
  clock = 11_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'prompt' }), '127.0.0.1');
  clock = 14_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'stop' }), '127.0.0.1');
  assert.equal((await route(h, 'prepare')).status, 200);
  const terminal = h.terminals[0], write = terminal.write.bind(terminal);
  terminal.write = data => { write(data); if (data === '\r') terminal.emitExit({ exitCode: 0 }); };
  assert.equal((await route(h, 'stop')).status, 503);
  assert.equal((await requestHttp(h, 'DELETE', '/agenthub/provider-switch', headers, JSON.stringify({ requestId }))).status, 409);
  assert.equal(terminal.killed, false);
  acceptUsage = true;
  const retried = await route(h, 'stop');
  assert.equal(retried.status, 200);
  assert.equal(JSON.parse(retried.body).stopped, true);
  assert.equal(terminal.writes.filter(value => value === '/exit').length, 1);
  assert.equal(terminal.killed, false);
});

test('cancelling a reserved source wakes an idle source inbox', async () => {
  let clock = 10_000;
  const file = '/home/agent/.test-agent/source.jsonl';
  const h = createHarness({ ...env, AGENTHUB_CALLBACK_URL: 'https://example.com/session' },
    { supportsProviderSwitch: true, turnHooks: true, findTranscript: () => file, nativeIdentity: () => 'source-native' },
    { files: { [file]: '{"type":"user","message":{"role":"user","content":"source"}}\n' }, now: () => clock,
      fetchResponse: () => ({ ok: true, json: async () => ({ messages: [] }), text: async () => '' }) });
  const socket = new FakeSocket(); h.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', JSON.stringify({ type: 'resize', cols: 120, rows: 36 }));
  clock = 11_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'prompt' }), '127.0.0.1');
  clock = 14_000;
  await requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event: 'stop' }), '127.0.0.1');
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal((await route(h, 'prepare')).status, 200);
  h.requests.length = 0;
  assert.equal((await requestHttp(h, 'DELETE', '/agenthub/provider-switch', headers, JSON.stringify({ requestId }))).status, 200);
  h.intervals.find(interval => interval.ms === 1000).callback();
  await new Promise(resolve => setTimeout(resolve, 10));
  assert(h.requests.some(request => request.url.includes('/messages?via=next_turn')));
});

test('source cancellation is idempotent even before reservation reaches the runtime', async () => {
  const h = createHarness(env, { supportsProviderSwitch: true, turnHooks: true });
  for (let attempt = 0; attempt < 2; attempt++)
    assert.equal((await requestHttp(h, 'DELETE', '/agenthub/provider-switch', headers, JSON.stringify({ requestId }))).status, 200);
});
