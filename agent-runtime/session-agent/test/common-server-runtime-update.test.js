'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const { createHarness, createChatHarness, requestHttp, FakeSocket, tick } = require('./common-server-harness');
const env = { AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_STATE_PUT_URL: 'https://example.com/state' };
const headers = { 'X-Agent-Token': 'correct-token' };
const drain = h => requestHttp(h, 'POST', '/agenthub/runtime-drain', headers);

test('runtime update fails closed for terminal transports and unauthorized requests', async () => {
  const h = createHarness(env);
  assert.equal((await drain(h)).status, 409);
  assert.equal((await requestHttp(h, 'POST', '/agenthub/runtime-drain', {})).status, 401);
  assert.equal((await requestHttp(h, 'DELETE', '/agenthub/runtime-drain', {})).status, 401);
});

test('busy chat cannot drain; completed chat blocks new input until release', async () => {
  const h = createChatHarness(env);
  const socket = new FakeSocket();
  h.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', JSON.stringify({ type: 'chat', text: 'do work' }));
  assert.equal((await drain(h)).status, 409);
  await tick();
  assert.equal((await drain(h)).status, 409);
  h.children[0].emitStdout('{"type":"assistant"}\n');
  assert.equal((await drain(h)).status, 409);
  h.children[0].emitStdout('{"type":"result"}\n');
  assert.equal((await drain(h)).status, 200);
  const writes = h.children[0].stdinWrites.length;
  socket.emit('message', JSON.stringify({ type: 'chat', text: 'must wait' }));
  const fleet = await requestHttp(h, 'POST', '/agenthub/messages', headers,
    JSON.stringify({ body: 'must wait', priority: true }));
  assert.equal(fleet.status, 409);
  await tick();
  assert.equal(h.children[0].stdinWrites.length, writes);
  assert.equal((await requestHttp(h, 'DELETE', '/agenthub/runtime-drain', headers)).status, 200);
  socket.emit('message', JSON.stringify({ type: 'chat', text: 'continue' }));
  await tick();
  assert.equal(h.children[0].stdinWrites.length, writes + 1);
});

test('failed state upload refuses replacement and reopens input', async () => {
  const h = createChatHarness(env, {}, {
    execResult: (_script, file) => file === 'curl' ? { error: new Error('upload failed') } : {}
  });
  assert.equal((await drain(h)).status, 503);
  const socket = new FakeSocket();
  h.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', JSON.stringify({ type: 'chat', text: 'continue working' }));
  await tick();
  assert.equal(h.children[0].stdinWrites.length, 1);
});

test('the initial prompt counts as a turn until its structured result arrives', async () => {
  const h = createChatHarness({ ...env, AGENTHUB_PROMPT: 'initial work' }, {
    isResumeCommand: () => false
  });
  await tick();
  assert.equal((await drain(h)).status, 409);
  h.children[0].emitStdout('{"type":"result"}\n');
  assert.equal((await drain(h)).status, 200);
});
