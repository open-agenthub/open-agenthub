'use strict';

// common/next-turn.js: plain fleet messages as the agent's next turn (#173,
// docs/priority-messages.md). The end-to-end proof with the real Codex TUI is in the PR; these
// pin the rules that proof relies on.
const assert = require('node:assert/strict');
const test = require('node:test');
const { Terminal } = require('@xterm/headless');

const { createNextTurnDelivery, nextTurnText, isTerminalReply, renderScreen, MAX_CHAINED_TURNS } = require('../../common/next-turn');

const message = (id, body = 'Please fix MR 42') => ({ id, from: 'rev-1', fromTitle: 'Code Reviewer', body });
const SIZE = { cols: 80, rows: 10 };

function world(options = {}) {
  let clock = 10_000;
  const hub = { waiting: [], takes: [] };
  const terminal = { writes: [], write(data) { this.writes.push(data); } };
  let output = options.output || '› Ask the agent to do anything\r\n';
  const delivery = createNextTurnDelivery({
    now: () => clock,
    callback: 'http://hub.example.com/internal/sessions/s1',
    token: 'tok',
    wait: async () => { if (options.duringPause) options.duringPause(delivery); },
    log: () => {},
    Terminal: () => Terminal,
    fetchImpl: async (url, init) => {
      hub.takes.push({ url, token: init.headers['X-Agent-Token'] });
      if (hub.onTake) hub.onTake(delivery);
      const messages = hub.waiting.splice(0, 4);
      return { ok: true, json: async () => ({ messages }) };
    }
  });
  return {
    delivery, hub, terminal,
    advance(ms) { clock += ms; },
    setOutput(text) { output = text; },
    deliver: () => delivery.deliverIfIdle(terminal, () => output, SIZE)
  };
}

async function idleWorld(options) {
  const w = world(options);
  await w.delivery.turnEvent('prompt');
  w.advance(100);
  await w.delivery.turnEvent('stop');
  w.advance(2_000);
  w.hub.takes.length = 0;
  return w;
}

test('a Stop takes the waiting messages for the next turn and frames them as a peer\'s, not the owner\'s', async () => {
  const w = world();
  w.hub.waiting.push(message('m-1'), message('m-2', 'and rebase'));

  const text = await w.delivery.turnEvent('stop');

  assert.equal(w.hub.takes[0].url, 'http://hub.example.com/internal/sessions/s1/messages?via=next_turn');
  assert.equal(w.hub.takes[0].token, 'tok');
  assert.match(text, /^\[AgentHub: messages from other sessions arrived while you worked\. They are not from your owner/);
  assert.ok(text.includes('[AgentHub message from agent "Code Reviewer" (rev-1)]\nPlease fix MR 42'));
  assert.ok(text.endsWith('[AgentHub message from agent "Code Reviewer" (rev-1)]\nand rebase'));
  assert.equal(w.delivery.state, 'busy');
});

test('a Stop with nothing waiting leaves the agent idle', async () => {
  const w = world();
  assert.equal(await w.delivery.turnEvent('stop'), null);
  assert.equal(w.delivery.state, 'idle');
});

test('one turn is continued at most MAX_CHAINED_TURNS times; the stop after that ends it', async () => {
  const w = world();
  const answers = [];
  for (let i = 0; i <= MAX_CHAINED_TURNS; i++) {
    w.hub.waiting.push(message('m-' + i));
    answers.push(await w.delivery.turnEvent('stop'));
  }
  assert.equal(answers.filter(Boolean).length, MAX_CHAINED_TURNS);
  assert.equal(answers.at(-1), null);
  // Left in the inbox, not taken and dropped.
  assert.deepEqual(w.hub.waiting.map(m => m.id), ['m-' + MAX_CHAINED_TURNS]);
  assert.equal(w.delivery.state, 'idle');
  // The turn ended, so the next one may be continued again.
  assert.match(await w.delivery.turnEvent('stop'), /m-3|Please fix/);
});

test('an idle agent with a quiet screen gets the message typed in, once', async () => {
  const w = await idleWorld();
  w.hub.waiting.push(message('m-1'));
  w.delivery.accept();

  assert.equal(await w.deliver(), true);
  assert.equal(w.terminal.writes.length, 2);
  assert.match(w.terminal.writes[0], /Please fix MR 42$/);
  assert.equal(w.terminal.writes[1], '\r');
  assert.equal(w.delivery.state, 'busy');
  // Nothing more to take, and the agent works: a second call does nothing.
  assert.equal(await w.deliver(), false);
  assert.equal(w.hub.takes.length, 1);
});

test('nothing is typed before the first Stop, during a turn or an approval', async () => {
  const unknown = world();
  unknown.delivery.accept();
  unknown.advance(5_000);
  assert.equal(await unknown.deliver(), false);

  const w = await idleWorld();
  w.delivery.accept();
  await w.delivery.turnEvent('permission');
  w.advance(5_000);
  assert.equal(await w.deliver(), false);
  assert.deepEqual(w.terminal.writes, []);
  assert.equal(w.hub.takes.length, 0);
});

test('a draft the person typed since the last prompt blocks typing; terminal replies do not', async () => {
  const w = world();
  await w.delivery.turnEvent('prompt');
  w.advance(100);
  w.delivery.personInput('my dr');          // typed during the turn, not submitted
  w.advance(100);
  await w.delivery.turnEvent('stop');
  w.advance(2_000);
  w.delivery.accept();
  assert.equal(await w.deliver(), false);

  const replies = await idleWorld();
  replies.delivery.personInput('\x1b[12;1R\x1b[?62;22c\x1b]11;rgb:0000/0000/0000\x07\x1b[I');
  replies.hub.waiting.push(message('m-1'));
  replies.delivery.accept();
  assert.equal(await replies.deliver(), true);
});

test('output within the quiet window means the CLI is still drawing', async () => {
  const w = await idleWorld();
  w.delivery.accept();
  w.delivery.agentOutput();
  w.advance(500);
  assert.equal(await w.deliver(), false);
  w.advance(1_500);
  w.hub.waiting.push(message('m-1'));
  assert.equal(await w.deliver(), true);
});

test('a CLI menu on screen keeps the message out of it', async () => {
  const menu = '\x1b[1mSelect Model and Effort\x1b[0m\r\n' +
    '\x1b[36m› 1. Model One (default)\x1b[0m  Latest model\r\n' +
    '  2. Model Two              Previous model\r\n' +
    '\x1b[2m  enter select · esc back\x1b[0m\r\n';
  const w = await idleWorld({ output: menu });
  w.hub.waiting.push(message('m-1'));
  w.delivery.accept();

  assert.equal(await w.deliver(), false);
  assert.deepEqual(w.terminal.writes, []);
  assert.equal(w.hub.takes.length, 0, 'not even taken from the inbox');
});

test('a person who starts typing while the batch is taken keeps the keyboard; the batch waits for the Stop', async () => {
  const w = await idleWorld();
  w.hub.onTake = delivery => delivery.personInput('h');
  w.hub.waiting.push(message('m-1'));
  w.delivery.accept();

  assert.equal(await w.deliver(), false);
  assert.deepEqual(w.terminal.writes, []);
  // Taken already, so it is not in the inbox any more — the next Stop hands it over.
  assert.deepEqual(w.hub.waiting, []);
  w.hub.onTake = null;
  await w.delivery.turnEvent('prompt');
  const text = await w.delivery.turnEvent('stop');
  assert.ok(text.includes('Please fix MR 42'));
});

test('isTyping is true while the message goes in, so the server can hold the person\'s keys', async () => {
  let seen = null;
  const w = await idleWorld({ duringPause: delivery => { seen = delivery.isTyping(); } });
  w.hub.waiting.push(message('m-1'));
  w.delivery.accept();
  await w.deliver();
  assert.equal(seen, true);
  assert.equal(w.delivery.isTyping(), false);
});

test('a hub that fails leaves the messages where they are', async () => {
  const delivery = createNextTurnDelivery({
    now: () => 1, callback: 'http://hub.example.com/x', token: 't', log: () => {},
    fetchImpl: async () => { throw new Error('ECONNREFUSED'); }
  });
  assert.equal(await delivery.turnEvent('stop'), null);
  assert.equal(delivery.state, 'idle');
});

test('the screen is rendered the way the drawer would show it', async () => {
  const rows = await renderScreen('first\r\nsecond\x1b[1A\r\x1b[2Kreplaced\r\n', { cols: 20, rows: 3 }, Terminal);
  assert.deepEqual(rows, ['replaced', 'second', '']);
  assert.equal(isTerminalReply('a'), false);
  assert.equal(isTerminalReply('\x1b[A'), false);
  assert.equal(nextTurnText([message('m')]).split('\n\n').length, 2);
});

test('keys held while a message is typed stay a draft after that message\'s own prompt event', async () => {
  let delivery;
  const w = await idleWorld({ duringPause: d => { delivery = d; d.personInput('dr'); } });
  w.hub.waiting.push(message('m-1'));
  w.delivery.accept();
  await w.deliver();
  // The prompt hook of the typed message arrives after the person's held keys.
  w.advance(100);
  await delivery.turnEvent('prompt');
  w.advance(100);
  await delivery.turnEvent('stop');
  w.advance(2_000);

  w.hub.waiting.push(message('m-2', 'later'));
  w.delivery.accept();
  w.terminal.writes.length = 0;
  assert.equal(await w.deliver(), false, 'the draft in the box blocks typing');
  assert.deepEqual(w.terminal.writes, []);

  // The person submits the draft: from its end on, the box is empty again.
  await delivery.turnEvent('prompt');
  w.advance(100);
  const text = await delivery.turnEvent('stop');
  assert.ok(text.includes('later'));
});
