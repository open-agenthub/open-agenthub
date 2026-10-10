'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { injectTerminalInput, formatFleetMessage, ESCAPE } = require('../../common/terminal-inject');

function recorder() {
  const events = [];
  return {
    events,
    term: { write(data) { events.push(['write', data]); } },
    wait(ms) { events.push(['wait', ms]); return Promise.resolve(); }
  };
}

test('typed input is text, a pause, then Enter on its own', async () => {
  const { events, term, wait } = recorder();

  await injectTerminalInput(term, 'hello\nworld', { wait });

  assert.deepEqual(events, [['write', 'hello\nworld'], ['wait', 300], ['write', '\r']]);
});

test('an interrupt is Escape, a pause, and then the usual typing', async () => {
  const { events, term, wait } = recorder();

  await injectTerminalInput(term, 'stop', { interrupt: true, wait });

  assert.deepEqual(events, [['write', ESCAPE], ['wait', 300], ['write', 'stop'], ['wait', 300], ['write', '\r']]);
});

test('the pauses are real timers when none is injected', async () => {
  const writes = [];
  const started = Date.now();
  await injectTerminalInput({ write: data => writes.push(data) }, 'x');
  assert.deepEqual(writes, ['x', '\r']);
  assert.ok(Date.now() - started >= 250);
});

test('the header names the sending agent, or says the message came from outside the fleet', () => {
  assert.equal(formatFleetMessage({ from: 'rev-1', fromTitle: 'Reviewer', body: 'look' }),
    '[AgentHub message from agent "Reviewer" (rev-1)]\nlook');
  assert.equal(formatFleetMessage({ from: 'rev-1', fromTitle: null, body: 'look' }),
    '[AgentHub message from agent "rev-1" (rev-1)]\nlook');
  assert.equal(formatFleetMessage({ from: null, body: 'look' }),
    '[AgentHub message from outside the fleet]\nlook');
});
