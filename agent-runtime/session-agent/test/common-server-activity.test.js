'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const { createHarness, requestHttp, FakeSocket } = require('./common-server-harness');
const headers = { 'X-Agent-Token': 'activity-token', 'Content-Type': 'application/json' };
const subscribe = socket => socket.emit('message', JSON.stringify({ type: 'subscribe', channel: 'activity' }));
const frames = socket => socket.sent.map(data => { try { return JSON.parse(data); } catch { return null; } }).filter(f => f?.subtype === 'activity');
const turn = (h, event) => requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event }), '127.0.0.1');
test('activity is opt-in, follows hooks, and is never mixed into legacy replay', async () => {
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'activity-token' }, { turnHooks: true });
  const legacy = new FakeSocket(), viewer = new FakeSocket();
  h.runtime.webSocketServer.connect(legacy, '/'); h.runtime.webSocketServer.connect(viewer, '/');
  subscribe(viewer);
  assert.equal(frames(viewer).at(-1).state, 'unknown');
  await turn(h, 'prompt'); assert.equal(frames(viewer).at(-1).state, 'working');
  await turn(h, 'permission'); assert.equal(frames(viewer).at(-1).state, 'waiting');
  viewer.emit('message', JSON.stringify({ type: 'input', data: '\r' }));
  assert.equal(frames(viewer).at(-1).state, 'working');
  await turn(h, 'stop'); assert.equal(frames(viewer).at(-1).state, 'idle');
  assert.deepEqual(frames(legacy), []);
  h.terminals[0].emitData('result text');
  const reconnected = new FakeSocket(); h.runtime.webSocketServer.connect(reconnected, '/'); subscribe(reconnected);
  assert.equal(frames(reconnected).at(-1).state, 'idle');
  assert.equal(frames(reconnected).length, 1);
  const before = viewer.sent.length; viewer.emit('close'); await turn(h, 'prompt');
  assert.equal(viewer.sent.length, before);
});
test('a runtime without hooks reports recent output and then expires it', () => {
  let clock = 10000;
  const h = createHarness({}, {}, { now: () => clock });
  const viewer = new FakeSocket(); h.runtime.webSocketServer.connect(viewer, '/'); subscribe(viewer);
  h.terminals[0].emitData('working output');
  h.intervals.find(i => i.ms === 500).callback();
  assert.equal(frames(viewer).at(-1).state, 'working');
  assert.equal(frames(viewer).at(-1).source, 'output');
  clock += 2000; h.intervals.find(i => i.ms === 500).callback();
  assert.equal(frames(viewer).at(-1).state, 'idle');
});

test('interrupts and failed plugin turns clear presentation without enabling a fleet continuation', async () => {
  let takes = 0;
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'activity-token' }, { turnHooks: true }, { fetchResponse: url => {
    if (!url.includes('/messages')) return undefined;
    takes++; return { ok: true, json: async () => ({ messages: [] }) };
  } });
  const viewer = new FakeSocket(); h.runtime.webSocketServer.connect(viewer, '/'); subscribe(viewer);
  await turn(h, 'prompt');
  viewer.emit('message', JSON.stringify({ type: 'input', data: '\x1b' }));
  assert.equal(frames(viewer).at(-1).state, 'working');
  viewer.emit('message', JSON.stringify({ type: 'input', data: '\x03' }));
  assert.equal(frames(viewer).at(-1).state, 'working');
  await turn(h, 'idle');
  assert.equal(frames(viewer).at(-1).state, 'idle');
  h.terminals[0].emitData('prompt redraw'); h.intervals.find(i => i.ms === 500).callback();
  assert.equal(frames(viewer).at(-1).state, 'idle');
  await turn(h, 'prompt'); assert.equal(frames(viewer).at(-1).state, 'working');
  await turn(h, 'idle'); assert.equal(frames(viewer).at(-1).state, 'idle');
  assert.equal(takes, 0);
});

test('verified provider interruption keys clear presentation only, also in a permission or question dialog', async () => {
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'activity-token' }, {
    turnHooks: true, isTurnInterrupt: data => data === '\x1b'
  });
  const socket = new FakeSocket(); h.runtime.webSocketServer.connect(socket, '/'); subscribe(socket);
  await turn(h, 'prompt');
  socket.emit('message', JSON.stringify({ type: 'input', data: '\x1b' }));
  assert.equal(frames(socket).at(-1).state, 'idle');
  await turn(h, 'prompt');
  assert.equal(frames(socket).at(-1).state, 'working');
  // Claude Code 2.1.287 ends the turn on Escape in either dialog without running a hook (#252);
  // kept waiting, the Workspace asked for an approval at an idle prompt.
  await turn(h, 'permission');
  assert.deepEqual(frames(socket).at(-1), { type: 'agenthub', subtype: 'activity', state: 'waiting', source: 'hooks', ready: true, reason: 'approval' });
  socket.emit('message', JSON.stringify({ type: 'input', data: '\x1b' }));
  assert.equal(frames(socket).at(-1).state, 'idle');
  await turn(h, 'prompt');
  await turn(h, 'question');
  assert.deepEqual(frames(socket).at(-1), { type: 'agenthub', subtype: 'activity', state: 'waiting', source: 'hooks', ready: true, reason: 'answer' });
  socket.emit('message', JSON.stringify({ type: 'input', data: '\x1b' }));
  assert.equal(frames(socket).at(-1).state, 'idle');
});

test('a question waits for an answer, and the answer resumes the turn', async () => {
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'activity-token' }, { turnHooks: true });
  const socket = new FakeSocket(); h.runtime.webSocketServer.connect(socket, '/'); subscribe(socket);
  await turn(h, 'prompt');
  await turn(h, 'question');
  assert.equal(frames(socket).at(-1).reason, 'answer');
  socket.emit('message', JSON.stringify({ type: 'input', data: '\r' }));
  assert.deepEqual(frames(socket).at(-1), { type: 'agenthub', subtype: 'activity', state: 'working', source: 'hooks', ready: true });
});

test('a prompt clears the previous hub question before the native hook returns', async () => {
  const callbacks = [];
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'activity-token', AGENTHUB_CALLBACK_URL: 'https://example.com/internal/sessions/s1' },
    { turnHooks: true }, { fetchResponse: (url, options) => {
      if (url.endsWith('/turn-started')) callbacks.push({ url, token: options.headers['X-Agent-Token'] });
      return undefined;
    } });
  await turn(h, 'prompt');
  await turn(h, 'permission');
  await turn(h, 'stop');
  assert.deepEqual(callbacks, [{ url: 'https://example.com/internal/sessions/s1/turn-started', token: 'activity-token' }]);
});

test('persistence control status is replayed only to subscribed viewers', () => {
  const h = createHarness();
  const legacy = new FakeSocket(), viewer = new FakeSocket();
  h.runtime.webSocketServer.connect(legacy, '/');
  h.runtime.webSocketServer.connect(viewer, '/');
  subscribe(viewer);
  const status = socket => socket.sent.map(data => { try { return JSON.parse(data); } catch { return null; } }).filter(frame => frame?.subtype === 'persistence');
  assert.deepEqual(status(legacy), []);
  assert.deepEqual(status(viewer), [{ type: 'agenthub', subtype: 'persistence', withheld: false }]);
});

// An idle Claude fires no hook before someone types, so its state stays 'unknown' while the CLI
// already reads input; `ready` is what the Workspace ends its starting state on (#306).
test('the activity answer says the CLI is running before its first hook, and not once it is gone', () => {
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'activity-token' }, { turnHooks: true });
  const viewer = new FakeSocket(); h.runtime.webSocketServer.connect(viewer, '/'); subscribe(viewer);
  assert.deepEqual(frames(viewer).at(-1), { type: 'agenthub', subtype: 'activity', state: 'unknown', source: 'hooks', ready: true });
  h.terminals[0].emitExit({ exitCode: 0 });
  h.intervals.find(i => i.ms === 500).callback();
  assert.equal(frames(viewer).at(-1).ready, false);
});
