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

// ---- Plain messages as the next turn, for runtimes whose hooks report turns (#173) ------------

const turnHeaders = { 'X-Agent-Token': 'correct-token', 'Content-Type': 'application/json' };
const CALLBACK = 'http://hub.example.com/internal/sessions/s1';

/** A turn-hook runtime with a clock the test moves and a hub inbox the session agent takes from. */
function turnHarness(environment = {}, options = {}) {
  let clock = 50_000;
  const hub = { waiting: [], takes: [] };
  const timers = [];
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_CALLBACK_URL: CALLBACK, ...environment },
    { turnHooks: true }, {
      now: () => clock,
      setTimeout: options.holdTimers ? (callback => { timers.push(callback); return timers.length; }) : undefined,
      fetchResponse: url => {
        if (!url.startsWith(CALLBACK + '/messages')) return undefined;
        hub.takes.push(url);
        const messages = hub.waiting.splice(0, 4);
        return { ok: true, json: async () => ({ messages }) };
      }
    });
  return {
    harness, hub, advance(ms) { clock += ms; },
    runTimers() { const due = timers.splice(0); for (const callback of due) callback(); }
  };
}
// The screen check renders with xterm, which parses on a timer of its own.
const settle = () => new Promise(resolve => setTimeout(resolve, 30));
async function eventually(condition) {
  for (let i = 0; i < 200 && !condition(); i++) await settle();
}
const turn = (harness, event, address = '127.0.0.1', headers = turnHeaders) =>
  requestHttp(harness, 'POST', '/agenthub/turn', headers, JSON.stringify({ event }), address);
const intervalTick = harness => harness.intervals.find(entry => entry.ms === 1_000).callback();

test('a plain message for a turn-hook runtime waits for the turn instead of being typed or left to agent_inbox', async () => {
  const { harness } = turnHarness();

  const response = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ priority: false }));

  assert.deepEqual(JSON.parse(response.body), { delivered: 'next-turn' });
  // Before the first Stop nothing says the CLI waits at its prompt.
  assert.deepEqual(harness.terminals[0].writes, []);
});

test('runtimes without turn hooks, and autonomous runs, keep plain messages in the inbox', async () => {
  const autonomous = turnHarness({ AGENTHUB_MODE: 'autonomous' }).harness;
  const response = await requestHttp(autonomous, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ priority: false }));
  assert.deepEqual(JSON.parse(response.body), { delivered: 'unavailable', reason: 'not_priority' });

  const plain = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  assert.equal((await turn(plain, 'stop')).status, 404);
});

test('the turn route takes loopback callers with the callback token and known events only', async () => {
  const { harness } = turnHarness();

  const remote = await turn(harness, 'stop', '10.0.0.9');
  const wrongToken = await turn(harness, 'stop', '127.0.0.1', { ...turnHeaders, 'X-Agent-Token': 'nope' });
  const unknown = await turn(harness, 'sleep');
  const get = await requestHttp(harness, 'GET', '/agenthub/turn', turnHeaders, '', '::1');

  assert.deepEqual([remote.status, wrongToken.status, unknown.status, get.status], [401, 401, 400, 405]);
});

test('a Stop answers with the messages waiting in the hub inbox, taken as next_turn', async () => {
  const { harness, hub } = turnHarness();
  hub.waiting.push({ id: 'm-1', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'Please fix MR 42' });

  await turn(harness, 'prompt');
  const stop = await turn(harness, 'stop');
  const again = await turn(harness, 'stop');

  assert.deepEqual(hub.takes, [CALLBACK + '/messages?via=next_turn', CALLBACK + '/messages?via=next_turn']);
  const text = JSON.parse(stop.body).text;
  assert.match(text, /not from your owner/);
  assert.ok(text.endsWith(EXPECTED_TEXT));
  assert.deepEqual(JSON.parse(again.body), {});
  assert.deepEqual(harness.terminals[0].writes, []);
});

test('an idle agent gets the message typed in, with the person\'s keys held until after Enter', async () => {
  const { harness, hub, advance, runTimers } = turnHarness({}, { holdTimers: true });
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');
  await turn(harness, 'prompt');
  await turn(harness, 'stop');
  advance(2_000);

  hub.waiting.push({ id: 'm-1', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'Please fix MR 42' });
  await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ priority: false }));
  await eventually(() => harness.terminals[0].writes.length > 0);
  // The text is in, Enter waits for its pause — and the person presses a key right then.
  assert.equal(harness.terminals[0].writes.length, 1);
  socket.emit('message', JSON.stringify({ type: 'input', data: 'x' }));
  runTimers();
  await eventually(() => harness.terminals[0].writes.length >= 2);
  await settle();
  // Enter went out alone; the key still waits one more pause, so the TUI reads Enter by itself.
  assert.equal(harness.terminals[0].writes.length, 2);
  runTimers();
  await eventually(() => harness.terminals[0].writes.length >= 3);

  const writes = harness.terminals[0].writes;
  assert.match(writes[0], /Please fix MR 42$/);
  assert.equal(writes[1], '\r');
  assert.deepEqual(writes.slice(2), ['x']);
});

test('the one-second check delivers once the screen settles, and not while the agent is drawing', async () => {
  const { harness, hub, advance } = turnHarness();
  await turn(harness, 'prompt');
  await turn(harness, 'stop');
  harness.terminals[0].emitData('• Working (1s • esc to interrupt)');
  hub.waiting.push({ id: 'm-1', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'Please fix MR 42' });
  await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, fleetMessage({ priority: false }));
  await settle();
  assert.deepEqual(harness.terminals[0].writes, []);

  advance(2_000);
  intervalTick(harness);
  await eventually(() => harness.terminals[0].writes.length > 0);
  assert.match(harness.terminals[0].writes[0], /Please fix MR 42$/);
});
