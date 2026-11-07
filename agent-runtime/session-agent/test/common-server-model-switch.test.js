'use strict';

// POST /agenthub/model (#478): the session agent types the CLI's own model command at a safe
// point and answers only once the CLI confirmed it. The CLI is a fake terminal that prints what
// the pinned CLIs print (docs/design/live-model-switch.md).

const assert = require('node:assert/strict');
const test = require('node:test');

const { createHarness, requestHttp } = require('./common-server-harness');
const codex = require('../../codex/driver');
const claude = require('../../claude/driver');

const headers = { 'X-Agent-Token': 'correct-token', 'Content-Type': 'application/json' };
const turnHeaders = headers;

// A clock that moves on every read, and real but short timers: the switch polls the screen and
// gives up after its timeout, which has to come in a test's lifetime.
function switchHarness(driver, { environment = {}, answer } = {}) {
  let clock = 100_000;
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', ...environment },
    { turnHooks: true, modelSwitch: driver.modelSwitch, ...(driver.extra || {}) }, {
      now: () => (clock += 50),
      setTimeout: (callback, ms) => setTimeout(callback, ms >= 5_000 ? 1_000 : Math.min(ms, 5))
    });
  const terminal = harness.terminals[0];
  let typed = '';
  const writes = [];
  terminal.write = data => {
    writes.push(data);
    typed += data;
    if (data === '\r' && answer) {
      const line = typed;
      typed = '';
      const reply = answer(line);
      if (reply) setTimeout(() => terminal.emitData(reply), 10);
    }
  };
  return { harness, terminal, writes, advance: ms => { clock += ms; } };
}

const post = (harness, body, extra = headers) =>
  requestHttp(harness, 'POST', '/agenthub/model', extra, typeof body === 'string' ? body : JSON.stringify(body));
const turn = (harness, event) =>
  requestHttp(harness, 'POST', '/agenthub/turn', turnHeaders, JSON.stringify({ event }), '127.0.0.1');

test('a confirmed Codex switch answers with the model and the CLI\'s own line', async () => {
  const { harness, writes } = switchHarness(codex, {
    answer: line => line.startsWith('/model gpt-6-luna') ? '\r\n• Model changed to gpt-6-luna medium for this session only\r\n' : null
  });

  const response = await post(harness, { model: 'gpt-6-luna' });

  assert.equal(response.status, 200, response.body);
  assert.deepEqual(JSON.parse(response.body), {
    model: 'gpt-6-luna', confirmation: '• Model changed to gpt-6-luna medium for this session only' });
  assert.deepEqual(writes, ['/model gpt-6-luna', '\r']);
});

test('Claude Code\'s "Switch model?" dialog is answered once, then its confirmation counts', async () => {
  const { harness, writes } = switchHarness(claude, {
    answer: line => line.startsWith('/model ')
      ? '\r\n   Switch model?\r\n   ❯ 1. Yes, switch to Sonnet 5.5\r\n     2. No, go back\r\n'
      : line === '\r' ? '\x1b[2J\x1b[H  ⎿  Set model to Sonnet 5.5 and saved as your default for new sessions\r\n' : null
  });

  const response = await post(harness, { model: 'claude-sonnet-5-5' });

  assert.equal(response.status, 200, response.body);
  assert.match(JSON.parse(response.body).confirmation, /^⎿\s+Set model to Sonnet 5\.5/);
  assert.deepEqual(writes, ['/model claude-sonnet-5-5', '\r', '\r']);
});

test('a refusal from the CLI is reported as such, with its line', async () => {
  const { harness } = switchHarness(codex, {
    answer: () => "\r\n■ Model 'mock-b' is unavailable. Use /model to see available models.\r\n"
  });

  const response = await post(harness, { model: 'mock-b' });

  assert.equal(response.status, 422);
  assert.deepEqual(JSON.parse(response.body), {
    error: 'model_refused', detail: "■ Model 'mock-b' is unavailable. Use /model to see available models." });
});

test('a confirmation already in the scrollback from an earlier switch does not count again', async () => {
  const { harness, terminal, advance } = switchHarness(codex, { answer: () => null });
  terminal.emitData('• Model changed to gpt-6-luna medium for this session only\r\n');
  await new Promise(resolve => setTimeout(resolve, 20));
  // Past the quiet period after that output, which the switch otherwise waits out as a turn.
  advance(2_000);

  const response = await post(harness, { model: 'gpt-6-luna' });

  assert.equal(response.status, 504);
  assert.deepEqual(JSON.parse(response.body), { error: 'model_switch_not_confirmed' });
});

test('nothing is typed while a turn runs or a question or approval is open', async () => {
  for (const [event, reason] of [['prompt', 'turn_running'], ['question', 'question_open'], ['permission', 'approval_open']]) {
    const { harness, writes } = switchHarness(codex, { answer: () => 'unexpected' });
    assert.equal((await turn(harness, event)).status, 200);

    const response = await post(harness, { model: 'gpt-6-luna' });

    assert.equal(response.status, 409, event);
    assert.equal(JSON.parse(response.body).error, reason);
    assert.deepEqual(writes, [], event);
  }
});

test('a draft typed into the terminal since the last prompt blocks the switch', async () => {
  const { harness, writes } = switchHarness(codex, { answer: () => null });
  // The person's own keys arrive over the terminal socket, as the Workspace sends them.
  const { FakeSocket } = require('./common-server-harness');
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'input', data: 'half a sentence' })));

  const response = await post(harness, { model: 'gpt-6-luna' });

  assert.equal(response.status, 409);
  assert.equal(JSON.parse(response.body).error, 'draft_in_prompt');
  assert.deepEqual(writes.filter(data => data.startsWith('/model')), []);
});

test('ids that are not model ids, foreign fields and wrong tokens are refused before anything is typed', async () => {
  const { harness, writes } = switchHarness(codex, { answer: () => null });
  const cases = [
    [{ model: 'gpt 6' }, 400], [{ model: '-c' }, 400], [{ model: 'gpt\r/quit' }, 400], [{ model: 'x'.repeat(201) }, 400],
    [{ model: 'gpt-6', extra: 1 }, 400], ['not json', 400], [{}, 400]
  ];
  for (const [body, status] of cases) assert.equal((await post(harness, body)).status, status, JSON.stringify(body));
  assert.equal((await post(harness, { model: 'gpt-6' }, { ...headers, 'X-Agent-Token': 'nope' })).status, 401);
  assert.equal((await requestHttp(harness, 'GET', '/agenthub/model', headers)).status, 405);
  assert.deepEqual(writes, []);
});

test('a runtime without a switch, and a non-interactive run, say so', async () => {
  const none = switchHarness({ modelSwitch: undefined });
  assert.equal((await post(none.harness, { model: 'gpt-6' })).status, 400);
  const autonomous = switchHarness(codex, { environment: { AGENTHUB_MODE: 'autonomous' } });
  assert.deepEqual(JSON.parse((await post(autonomous.harness, { model: 'gpt-6' })).body), { error: 'model_switch_unsupported' });
});

test('a second switch while one waits for its confirmation is refused', async () => {
  let release;
  const { harness } = switchHarness(codex, {
    answer: () => { release = () => '\r\n• Model changed to gpt-6-luna medium\r\n'; return null; }
  });
  const first = post(harness, { model: 'gpt-6-luna' });
  await new Promise(resolve => setTimeout(resolve, 400));

  const second = await post(harness, { model: 'gpt-6-sol' });
  assert.equal(second.status, 409);
  assert.equal(JSON.parse(second.body).error, 'model_switch_pending');

  harness.terminals[0].emitData(release());
  assert.equal((await first).status, 200);
});
