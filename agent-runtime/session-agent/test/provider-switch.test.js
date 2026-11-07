'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { createHarness, requestHttp, FakeSocket } = require('./common-server-harness');
const requestId = '11111111-1111-4111-8111-111111111111';
const nextId = '22222222-2222-4222-8222-222222222222';
const headers = { 'X-Agent-Token': 'correct-token' };
const env = { AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_STATE_PUT_URL: 'https://example.com/state' };
const route = (h, action, id = requestId, nativeId) => requestHttp(h, 'POST', '/agenthub/provider-switch', headers, JSON.stringify({ requestId: id, action, nativeId }));

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
