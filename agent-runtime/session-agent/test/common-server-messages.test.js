'use strict';

// POST /agenthub/messages and the Claude Code mod's queue (docs/priority-messages.md). The
// harness is the one common-server.test.js builds; it is required from there so both files
// exercise exactly the same fake transport.
const assert = require('node:assert/strict');
const test = require('node:test');

const { createHarness, createChatHarness, requestHttp, FakeSocket, tick } = require('./common-server-harness');

const messageHeaders = { 'X-Agent-Token': 'correct-token', 'Content-Type': 'application/json' };
const fleetMessage = (extra = {}) => JSON.stringify({
  id: 'm-1', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'Please fix MR 42', priority: true, ...extra
});
const EXPECTED_TEXT = '[AgentHub message from agent "Code Reviewer" (rev-1)]\nPlease fix MR 42';

function delayRecorder() {
  const delays = [];
  return { delays, setTimeout(callback, ms) { delays.push(ms); callback(); return 1; } };
}

test('message route rejects a bad token, a wrong method, junk and an oversized body', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });

  const wrongToken = await requestHttp(harness, 'POST', '/agenthub/messages', { ...messageHeaders, 'X-Agent-Token': 'nope' }, fleetMessage());
  const get = await requestHttp(harness, 'GET', '/agenthub/messages', messageHeaders);
  const junk = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, 'not json');
  const empty = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, JSON.stringify({ body: '   ' }));
  const oversized = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders,
    JSON.stringify({ body: 'x'.repeat(70 * 1024), priority: true }));

  assert.deepEqual([wrongToken.status, get.status, junk.status, empty.status, oversized.status], [401, 405, 400, 400, 413]);
  assert.deepEqual(harness.terminals[0].writes, []);
});

test('a priority message is typed into the terminal with the hub pause before Enter', async () => {
  const timing = delayRecorder();
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, {}, timing);

  const response = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage());

  assert.equal(response.status, 200);
  assert.deepEqual(JSON.parse(response.body), { delivered: 'pty' });
  assert.deepEqual(harness.terminals[0].writes, [EXPECTED_TEXT, '\r']);
  assert.deepEqual(timing.delays, [300]);
});

test('an interrupt presses Escape first and waits before typing', async () => {
  const timing = delayRecorder();
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, {}, timing);

  const response = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ interrupt: true }));

  assert.deepEqual(JSON.parse(response.body), { delivered: 'pty' });
  assert.deepEqual(harness.terminals[0].writes, [String.fromCharCode(27), EXPECTED_TEXT, '\r']);
  assert.deepEqual(timing.delays, [300, 300]);
});

test('a message from outside the fleet is headed as such', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });

  await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders,
    JSON.stringify({ id: 'm-2', from: null, body: 'owner says hi', priority: true }));

  assert.equal(harness.terminals[0].writes[0], '[AgentHub message from outside the fleet]\nowner says hi');
});

test('a plain message without a mod stays in the inbox and touches no terminal', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });

  const response = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ priority: false }));

  assert.deepEqual(JSON.parse(response.body), { delivered: 'unavailable', reason: 'not_priority' });
  assert.deepEqual(harness.terminals[0].writes, []);
});

test('an autonomous -p run has no prompt to type into', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_MODE: 'autonomous' });

  const response = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ interrupt: true }));

  assert.deepEqual(JSON.parse(response.body), { delivered: 'unavailable', reason: 'non_interactive' });
  assert.deepEqual(harness.terminals[0].writes, []);
});

test('an ended agent reports the message as undeliverable', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });

  const response = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage());

  assert.deepEqual(JSON.parse(response.body), { delivered: 'unavailable', reason: 'agent_exited' });
});

test('chat transport feeds a priority message into the pipe as a user turn, interrupting first on request', async () => {
  const harness = createChatHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  const plain = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage());
  await tick();
  const stopping = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ id: 'm-2', interrupt: true }));
  await tick();

  assert.deepEqual(JSON.parse(plain.body), { delivered: 'chat' });
  assert.deepEqual(JSON.parse(stopping.body), { delivered: 'chat' });
  const writes = harness.children[0].stdinWrites.map(line => JSON.parse(line));
  assert.equal(writes[0].type, 'user');
  assert.equal(writes[0].message.content[0].text, EXPECTED_TEXT);
  assert.equal(writes[1].type, 'control_request');
  assert.equal(writes[1].request.subtype, 'interrupt');
  assert.equal(writes[2].type, 'user');
  // The echo reaches the chat UI like a typed message, so the person sees what the agent got.
  const echoes = socket.sent.map(line => JSON.parse(line.trim())).filter(event => event.agenthub_echo);
  assert.equal(echoes.length, 2);
  assert.equal(echoes[0].message.content[0].text, EXPECTED_TEXT);
});

// ---- The Claude Code mod's queue: /agenthub/mod/inbox and /agenthub/mod/heartbeat ------------

const modHeaders = { Authorization: 'Bearer mod-secret' };

function modHarness(environment = {}, harnessOptions = {}) {
  let clock = 1000;
  const time = { advance(ms) { clock += ms; } };
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_MOD_TOKEN: 'mod-secret', ...environment },
    {}, { now: () => clock, ...harnessOptions });
  return { harness, time };
}

test('mod routes exist only with a mod token and answer loopback callers with the right bearer', async () => {
  const without = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  assert.equal((await requestHttp(without, 'GET', '/agenthub/mod/inbox', modHeaders, '', '127.0.0.1')).status, 404);

  const { harness } = modHarness();
  const remote = await requestHttp(harness, 'GET', '/agenthub/mod/inbox', modHeaders, '', '10.0.0.9');
  const wrongToken = await requestHttp(harness, 'GET', '/agenthub/mod/inbox', { Authorization: 'Bearer other' }, '', '127.0.0.1');
  const callbackToken = await requestHttp(harness, 'GET', '/agenthub/mod/inbox', { Authorization: 'Bearer correct-token' }, '', '127.0.0.1');
  const inbox = await requestHttp(harness, 'GET', '/agenthub/mod/inbox', modHeaders, '', '::ffff:127.0.0.1');
  const heartbeat = await requestHttp(harness, 'POST', '/agenthub/mod/heartbeat', modHeaders, '', '::1');
  const heartbeatGet = await requestHttp(harness, 'GET', '/agenthub/mod/heartbeat', modHeaders, '', '127.0.0.1');

  assert.deepEqual([remote.status, wrongToken.status, callbackToken.status, inbox.status, heartbeat.status, heartbeatGet.status],
    [401, 401, 401, 200, 204, 405]);
  assert.deepEqual(JSON.parse(inbox.body), { messages: [] });
});

test('a live mod takes priority messages instead of the terminal, and the inbox hands them over once', async () => {
  const { harness } = modHarness();
  await requestHttp(harness, 'POST', '/agenthub/mod/heartbeat', modHeaders, '', '127.0.0.1');

  const queued = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ interrupt: true }));
  const first = await requestHttp(harness, 'GET', '/agenthub/mod/inbox', modHeaders, '', '127.0.0.1');
  const second = await requestHttp(harness, 'GET', '/agenthub/mod/inbox', modHeaders, '', '127.0.0.1');

  assert.deepEqual(JSON.parse(queued.body), { delivered: 'queued-for-mod' });
  assert.deepEqual(harness.terminals[0].writes, []);
  assert.deepEqual(JSON.parse(first.body), { messages: [
    { id: 'm-1', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'Please fix MR 42', priority: true, interrupt: true }
  ] });
  assert.deepEqual(JSON.parse(second.body), { messages: [] });
});

test('a mod that stopped polling loses the queue to the terminal after 15 seconds', async () => {
  const { harness, time } = modHarness();
  await requestHttp(harness, 'GET', '/agenthub/mod/inbox', modHeaders, '', '127.0.0.1');
  time.advance(14_000);
  const fresh = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage());
  time.advance(1_500);
  const stale = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ id: 'm-2' }));

  assert.deepEqual(JSON.parse(fresh.body), { delivered: 'queued-for-mod' });
  assert.deepEqual(JSON.parse(stale.body), { delivered: 'pty' });
  assert.deepEqual(harness.terminals[0].writes, [EXPECTED_TEXT, '\r']);
});

test('a live mod takes plain messages in an interactive session only', async () => {
  const interactive = modHarness();
  await requestHttp(interactive.harness, 'POST', '/agenthub/mod/heartbeat', modHeaders, '', '127.0.0.1');
  const autonomous = modHarness({ AGENTHUB_MODE: 'autonomous' });
  await requestHttp(autonomous.harness, 'POST', '/agenthub/mod/heartbeat', modHeaders, '', '127.0.0.1');

  const shown = await requestHttp(interactive.harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ priority: false }));
  const kept = await requestHttp(autonomous.harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ priority: false }));
  const pushed = await requestHttp(autonomous.harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ id: 'm-2' }));

  assert.deepEqual(JSON.parse(shown.body), { delivered: 'queued-for-mod' });
  assert.deepEqual(JSON.parse(kept.body), { delivered: 'unavailable', reason: 'not_priority' });
  assert.deepEqual(JSON.parse(pushed.body), { delivered: 'queued-for-mod' });
});
