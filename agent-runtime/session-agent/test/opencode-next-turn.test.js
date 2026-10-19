'use strict';

// opencode/next-turn.mjs: the policy plugin's half of the next-turn delivery (#192,
// docs/priority-messages.md). Message shapes as the plugin's client returns them on 1.18.35.
const assert = require('node:assert/strict');
const test = require('node:test');

const interactive = { AGENTHUB_MODE: 'interactive' };
const idle = { type: 'session.idle', properties: { sessionID: 'ses_1' } };
const done = { info: { role: 'assistant', finish: 'stop', time: { created: 1, completed: 2 } } };
// What session.idle sees right after Escape: the answer was stopped but not yet marked aborted.
const stopped = { info: { role: 'assistant', time: { created: 1 } } };

function world({ parentID, messages = [done], answer = null } = {}) {
  const reports = [];
  const prompts = [];
  const client = {
    session: {
      get: async () => ({ data: { id: 'ses_1', parentID } }),
      messages: async () => ({ data: [{ info: { role: 'user' } }, ...messages] }),
      promptAsync: async options => { prompts.push(options); return {}; }
    }
  };
  const reportImpl = async event => { reports.push(event); return event === 'stop' ? answer : null; };
  return { client, reports, prompts, reportImpl };
}

test('a finished top-level turn reports stop and submits the answer as the next prompt', async () => {
  const { createNextTurnRelay } = await import('../../opencode/next-turn.mjs');
  const w = world({ answer: '[AgentHub message from agent "Code Reviewer" (rev-1)]\nPlease fix MR 42' });
  const relay = createNextTurnRelay({ client: w.client, env: interactive, reportImpl: w.reportImpl });

  await relay.onEvent(idle);

  assert.deepEqual(w.reports, ['stop']);
  assert.deepEqual(w.prompts, [{ path: { id: 'ses_1' }, body: { parts: [
    { type: 'text', text: '[AgentHub message from agent "Code Reviewer" (rev-1)]\nPlease fix MR 42' }] } }]);
});

test('a stop with nothing waiting submits nothing', async () => {
  const { createNextTurnRelay } = await import('../../opencode/next-turn.mjs');
  const w = world();
  await createNextTurnRelay({ client: w.client, env: interactive, reportImpl: w.reportImpl }).onEvent(idle);
  assert.deepEqual(w.reports, ['stop']);
  assert.deepEqual(w.prompts, []);
});

test('subagents, stopped or failed turns, other events and autonomous runs report no stop', async () => {
  const { createNextTurnRelay } = await import('../../opencode/next-turn.mjs');
  const cases = [
    [world({ parentID: 'ses_0', answer: 'x' }), interactive, idle],
    [world({ messages: [stopped], answer: 'x' }), interactive, idle],
    [world({ messages: [{ info: { ...done.info, error: { name: 'MessageAbortedError' } } }], answer: 'x' }), interactive, idle],
    [world({ messages: [{ info: { ...done.info, error: { name: 'APIError' } } }], answer: 'x' }), interactive, idle],
    [world({ messages: [], answer: 'x' }), interactive, idle],
    [world({ answer: 'x' }), interactive, { type: 'session.status', properties: { sessionID: 'ses_1' } }],
    [world({ answer: 'x' }), { AGENTHUB_MODE: 'autonomous' }, idle]
  ];
  for (const [w, env, event] of cases) {
    await createNextTurnRelay({ client: w.client, env, reportImpl: w.reportImpl }).onEvent(event);
    const topLevelIdle = event.type === 'session.idle' && env.AGENTHUB_MODE === 'interactive' &&
      !((await w.client.session.get()).data.parentID);
    assert.deepEqual(w.reports, topLevelIdle ? ['idle'] : []);
    assert.deepEqual(w.prompts, []);
  }
});

test('a submitted prompt is reported, in interactive sessions only', async () => {
  const { createNextTurnRelay } = await import('../../opencode/next-turn.mjs');
  const w = world();
  await createNextTurnRelay({ client: w.client, env: interactive, reportImpl: w.reportImpl }).onChatMessage();
  await createNextTurnRelay({ client: w.client, env: { AGENTHUB_MODE: 'autonomous' }, reportImpl: w.reportImpl }).onChatMessage();
  assert.deepEqual(w.reports, ['prompt']);
});

test('the policy plugin wires the relay in and the driver declares turn hooks', async () => {
  const fs = require('node:fs');
  const path = require('node:path');
  const plugin = fs.readFileSync(path.join(__dirname, '..', '..', 'opencode', 'policy-plugin.mjs'), 'utf8');
  assert.match(plugin, /nextTurn\.onChatMessage\(\)/);
  assert.match(plugin, /nextTurn\.onEvent\(event\)/);
  assert.equal(require('../../opencode/driver.js').turnHooks, true);
});

test('the plugin reports each turn event once and lets the session agent deliver', async () => {
  const fs = require('node:fs');
  const path = require('node:path');
  const plugin = fs.readFileSync(path.join(__dirname, '..', '..', 'opencode', 'policy-plugin.mjs'), 'utf8');
  // A second reporter would make the session agent take messages for a stop whose answer nobody
  // submits; next-turn.mjs is the only one.
  assert.doesNotMatch(plugin, /fleet-turn-hook/);
  assert.notEqual(require('../../opencode/driver.js').fleetNextTurn, false);
});
