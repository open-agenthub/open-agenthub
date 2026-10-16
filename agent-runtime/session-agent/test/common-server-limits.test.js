'use strict';

// The usage-limit path through the session agent (docs/account-limits.md): the detector on the
// agent's output reports to the hub once per start, the Claude mod's loopback route relays its
// readings, and a live mod silences the output patterns.
const assert = require('node:assert/strict');
const test = require('node:test');

const { createHarness, createChatHarness, requestHttp } = require('./common-server-harness');

const patterns = [{ pattern: /You've hit your usage limit/, resetsAt: (match, text) => (/resets at (\S+)/.exec(text) || [])[1] || null }];
const modHeaders = { Authorization: 'Bearer mod-secret', 'Content-Type': 'application/json' };

function limitReports(harness) {
  return harness.requests
    .filter(request => request.url.endsWith('/account-exhausted'))
    .map(request => ({ headers: request.options.headers, body: JSON.parse(request.options.body) }));
}

test('a limit notice in the terminal is reported to the hub once per agent start, with the matched line', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_URL: 'http://hub/internal/sessions/s1', AGENTHUB_CALLBACK_TOKEN: 'correct-token' },
    { limitPatterns: patterns });

  harness.terminals[0].emitData('\x1b[31mYou\'ve hit your usage limit · resets at 2026-10-11T15:00:00Z\x1b[0m\r\n');
  harness.terminals[0].emitData('You\'ve hit your usage limit\r\n');

  const reports = limitReports(harness);
  assert.equal(reports.length, 1);
  assert.equal(reports[0].headers['X-Agent-Token'], 'correct-token');
  assert.deepEqual(reports[0].body, {
    source: 'output', kind: null, percentUsed: null,
    resetsAt: '2026-10-11T15:00:00.000Z',
    detail: 'You\'ve hit your usage limit · resets at 2026-10-11T15:00:00Z'
  });

  // A restart (here: a credential swap) arms the detector again.
  await requestHttp(harness, 'PUT', '/agenthub/credentials',
    { 'X-Agent-Token': 'correct-token', 'X-Agent-Provider': 'test', 'Content-Type': 'application/json' }, '{"token":"t"}');
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });
  harness.terminals[1].emitData('You\'ve hit your usage limit\r\n');
  assert.equal(limitReports(harness).length, 2);
});

test('a chat pipe reports a limit carried by a result event and stderr alike', async () => {
  const harness = createChatHarness({ AGENTHUB_CALLBACK_URL: 'http://hub/internal/sessions/s1', AGENTHUB_CALLBACK_TOKEN: 'correct-token' },
    { limitPatterns: patterns });

  harness.children[0].emitStdout('{"type":"assistant","message":{"content":[{"type":"text","text":"You\'ve hit your usage limit"}]}}\n');
  assert.equal(limitReports(harness).length, 0);
  harness.children[0].emitStderr('You\'ve hit your usage limit\n');
  assert.equal(limitReports(harness).length, 1);
  assert.equal(limitReports(harness)[0].body.detail, 'You\'ve hit your usage limit');
});

test('the mod relays a rate-limit reading through its loopback route, deduplicated per window', async () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'http://hub/internal/sessions/s1', AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_MOD_TOKEN: 'mod-secret'
  }, { limitPatterns: patterns });
  const reading = JSON.stringify({ kind: 'five_hour', percentUsed: 100, resetsAt: '2026-10-11T15:00:00Z' });

  const remote = await requestHttp(harness, 'POST', '/agenthub/mod/limit', modHeaders, reading, '10.0.0.9');
  const wrongToken = await requestHttp(harness, 'POST', '/agenthub/mod/limit', { ...modHeaders, Authorization: 'Bearer nope' }, reading, '127.0.0.1');
  const get = await requestHttp(harness, 'GET', '/agenthub/mod/limit', modHeaders, '', '127.0.0.1');
  const junk = await requestHttp(harness, 'POST', '/agenthub/mod/limit', modHeaders, '{"nothing":true}', '127.0.0.1');
  const first = await requestHttp(harness, 'POST', '/agenthub/mod/limit', modHeaders, reading, '127.0.0.1');
  const again = await requestHttp(harness, 'POST', '/agenthub/mod/limit', modHeaders, reading, '127.0.0.1');
  const later = await requestHttp(harness, 'POST', '/agenthub/mod/limit', modHeaders,
    JSON.stringify({ kind: 'five_hour', percentUsed: 100, resetsAt: '2026-10-11T20:00:00Z' }), '127.0.0.1');

  assert.deepEqual([remote.status, wrongToken.status, get.status, junk.status, first.status, again.status, later.status],
    [401, 401, 405, 400, 202, 202, 202]);
  const reports = limitReports(harness);
  assert.equal(reports.length, 2);
  assert.deepEqual(reports[0].body, { source: 'mod', kind: 'five_hour', percentUsed: 100, resetsAt: '2026-10-11T15:00:00.000Z', detail: null });
  assert.equal(reports[1].body.resetsAt, '2026-10-11T20:00:00.000Z');

  // The mod is alive now (its post counted as a heartbeat): the terminal patterns stay quiet.
  harness.terminals[0].emitData('You\'ve hit your usage limit\r\n');
  assert.equal(limitReports(harness).length, 2);
});

// ---- the switch itself: the reason line and messages that arrive mid-restart ------------------

const credentialHeaders = { 'X-Agent-Token': 'correct-token', 'X-Agent-Provider': 'test', 'Content-Type': 'application/json' };
const reason = 'Switched to account "Büro" because "Work" hit its usage limit (resets 14:00 UTC)';

test('a credential swap with a reason names it in the scrollback instead of the generic line', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });

  await requestHttp(harness, 'PUT', '/agenthub/credentials',
    { ...credentialHeaders, 'X-Agent-Switch-Reason': encodeURIComponent(reason + '\x1b[2J') }, '{"token":"t"}');
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });

  const socket = new (require('./common-server-harness').FakeSocket)();
  harness.runtime.webSocketServer.connect(socket, '/');
  assert.match(socket.sent[0], /\[agent\] Switched to account "Büro" because "Work" hit its usage limit \(resets 14:00 UTC\)\s+— restarting the agent and resuming the conversation\./);
  assert.doesNotMatch(socket.sent[0], /\x1b\[2J/);
  assert.doesNotMatch(socket.sent[0], /Provider account switched/);
});

test('in chat mode the reason arrives as an account-switched event', async () => {
  const harness = createChatHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  const { FakeSocket } = require('./common-server-harness');
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  await requestHttp(harness, 'PUT', '/agenthub/credentials',
    { ...credentialHeaders, 'X-Agent-Switch-Reason': encodeURIComponent(reason) }, '{"token":"t"}');
  harness.children[0].emitExit(0, null);

  const events = socket.sent.map(line => JSON.parse(line.trim()));
  const switched = events.find(event => event.type === 'agenthub' && event.subtype === 'account-switched');
  assert.ok(switched);
  assert.match(switched.text, /^Switched to account "Büro"/);
});

test('a priority message that arrives while the agent restarts is typed into the new terminal after a pause', async () => {
  const delays = [];
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, {}, {
    setTimeout(callback, ms) { delays.push(ms); callback(); return 1; }
  });
  const messageHeaders = { 'X-Agent-Token': 'correct-token', 'Content-Type': 'application/json' };
  const body = JSON.stringify({ id: 'm-1', from: null, body: reason, priority: true });

  await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '{"token":"t"}');
  const during = await requestHttp(harness, 'POST', '/agenthub/messages', messageHeaders, body);
  assert.deepEqual(JSON.parse(during.body), { delivered: 'pty', deferred: true });
  // Nothing went into the terminal that is on its way out.
  assert.deepEqual(harness.terminals[0].writes, []);

  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });
  // The new terminal gets it, after the TUI has had time to draw its prompt.
  await new Promise(resolve => setImmediate(resolve));
  assert.ok(delays.includes(3000));
  assert.deepEqual(harness.terminals[1].writes, ['[AgentHub message from outside the fleet]\n' + reason, '\r']);
});

test('without a mod token the limit route does not exist, and without a callback nothing is reported', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, { limitPatterns: patterns });
  const response = await requestHttp(harness, 'POST', '/agenthub/mod/limit', modHeaders, '{"kind":"five_hour","percentUsed":100}', '127.0.0.1');
  assert.equal(response.status, 404);

  harness.terminals[0].emitData('You\'ve hit your usage limit\r\n');
  assert.equal(limitReports(harness).length, 0);
});
