'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createContextClear, validateFollowUp, DELIVERY_ATTEMPTS, FALLBACK_ROUNDS } = require('../../common/context-clear');

const HANDOVER = '[AgentHub context handover]\n';

// A runtime stand-in: actions wait in `queue` until the test runs them, the store is a value,
// and every step is recorded. `transcript` is what verify reads: the texts the CLI took.
function make(overrides = () => ({})) {
  const r = {
    queue: [], steps: [], errors: [], delivered: [], logs: [], saved: null, transcript: [],
    store: {
      load: () => r.saved,
      save: state => { r.steps.push('save:' + (state.cleared ? 'cleared' : 'scheduled')); r.saved = state; },
      remove: () => { r.steps.push('remove'); r.saved = null; }
    }
  };
  r.queueImpl = createContextClear({
    store: r.store,
    scheduleAtSafePoint: (action, options) => r.queue.push({ action, options }),
    clear: async () => { r.steps.push('clear'); return 'old'; },
    waitForClear: async () => { r.steps.push('confirm'); },
    waitForInput: async () => { r.steps.push('ready'); },
    deliver: async (text, { attempt }) => { r.steps.push('type#' + attempt); r.transcript.push(text); },
    verify: async text => r.transcript.includes(text) ? { fresh: true } : null,
    onError: (error, info) => r.errors.push({ message: error.message, ...info }),
    onDelivered: info => r.delivered.push(info),
    log: line => r.logs.push(line),
    ...overrides(r)
  });
  r.runNext = async () => r.queue.shift().action();
  return r;
}

test('the handover is saved before anything is typed and removed once the transcript has it', async () => {
  const r = make();
  const result = r.queueImpl.schedule('/plan is the next topic. State: tests passed.');
  assert.equal(result.scheduled, true);
  assert.match(result.handover, /first message of the fresh conversation/);
  assert.deepEqual(r.saved, { followUp: '/plan is the next topic. State: tests passed.', cleared: false });
  assert.deepEqual(r.steps, ['save:scheduled']);
  assert.throws(() => r.queueImpl.schedule('duplicate'), /context_clear_pending/);
  assert.equal(await r.runNext(), true);
  assert.deepEqual(r.steps, ['save:scheduled', 'clear', 'save:cleared', 'confirm', 'ready', 'type#1', 'remove']);
  assert.deepEqual(r.transcript, [HANDOVER + '/plan is the next topic. State: tests passed.']);
  assert.equal(r.saved, null);
  assert.equal(r.queueImpl.pending, false);
  assert.deepEqual(r.delivered, [{ fresh: true, attempt: 1, round: 0 }]);
});

test('an input the CLI dropped is typed again', async () => {
  let dropped = false;
  const r = make(r => ({
    deliver: async (text, { attempt }) => {
      r.steps.push('type#' + attempt);
      if (!dropped) { dropped = true; return; }
      r.transcript.push(text);
    }
  }));
  r.queueImpl.schedule('State and next tasks');
  assert.equal(await r.runNext(), true);
  assert.deepEqual(r.steps.filter(s => s.startsWith('type')), ['type#1', 'type#2']);
  assert.equal(r.transcript.length, 1);
  assert.equal(r.saved, null);
});

test('a readiness check that throws after the clear does not lose the handover (#336)', async () => {
  let notReady = 2;
  const r = make(r => ({
    waitForInput: async () => { r.steps.push('ready'); if (notReady-- > 0) throw new Error('context_clear_input_not_ready'); }
  }));
  r.queueImpl.schedule('State and next tasks');
  assert.equal(await r.runNext(), true);
  assert.deepEqual(r.transcript, [HANDOVER + 'State and next tasks']);
  assert.deepEqual(r.errors, []);
});

test('an unconfirmed clear still sends the handover: holding it back would lose it if the clear did happen', async () => {
  const r = make(() => ({ waitForClear: async () => { throw new Error('context_clear_identity_unchanged'); } }));
  r.queueImpl.schedule('State and next tasks');
  assert.equal(await r.runNext(), true);
  assert.equal(r.transcript.length, 1);
  assert.match(r.logs.join('\n'), /not confirmed \(context_clear_identity_unchanged\)/);
});

test('spent attempts report the failure and retry at the next safe point without clearing again', async () => {
  let blocked = true;
  const r = make(r => ({
    waitForInput: async () => { r.steps.push('ready'); if (blocked) throw new Error('context_clear_input_not_ready'); }
  }));
  r.queueImpl.schedule('State and next tasks');
  assert.equal(await r.runNext(), false);
  assert.equal(r.steps.filter(s => s === 'ready').length, DELIVERY_ATTEMPTS);
  assert.deepEqual(r.errors, [{ message: 'context_clear_input_not_ready', retry: true, deferred: false }]);
  assert.equal(r.queueImpl.pending, true);
  assert.equal(r.queue.length, 1);
  assert.deepEqual(r.saved, { followUp: 'State and next tasks', cleared: true });
  blocked = false;
  r.steps.length = 0;
  assert.equal(await r.runNext(), true);
  assert.deepEqual(r.steps, ['ready', 'type#1', 'remove']);
  assert.deepEqual(r.delivered, [{ fresh: true, attempt: 1, round: 1 }]);
  assert.equal(r.queueImpl.pending, false);
});

test('after the last fallback round the handover stays saved for the next agent start', async () => {
  const r = make(() => ({ waitForInput: async () => { throw new Error('context_clear_input_not_ready'); } }));
  r.queueImpl.schedule('State and next tasks');
  for (let round = 0; round <= FALLBACK_ROUNDS; round++) assert.equal(await r.runNext(), false);
  assert.equal(r.queue.length, 0);
  assert.equal(r.errors.at(-1).retry, false);
  assert.equal(r.queueImpl.pending, false);
  assert.deepEqual(r.saved, { followUp: 'State and next tasks', cleared: true });
});

test('failed clears never type the handover into the old context; an agent start sends it after clearing', async () => {
  let gone = true;
  const r = make(r => ({
    clear: async () => { r.steps.push('clear'); if (gone) throw new Error('context_clear_unavailable'); return 'old'; }
  }));
  r.queueImpl.schedule('State and next tasks');
  assert.equal(await r.runNext(), false);
  assert.deepEqual(r.transcript, []);
  assert.deepEqual(r.errors, [{ message: 'context_clear_unavailable', retry: false, deferred: true }]);
  assert.equal(r.queueImpl.pending, false);
  assert.equal(r.queue.length, 0);
  gone = false;
  assert.equal(r.queueImpl.resume(), true);
  assert.equal(r.queue[0].options.atStart, true);
  assert.equal(r.queueImpl.resume(), false, 'one queued handover at a time');
  assert.equal(await r.runNext(), true);
  assert.deepEqual(r.transcript, [HANDOVER + 'State and next tasks']);
  assert.equal(r.saved, null);
});

test('a restart between the clear and the handover finds it saved', async () => {
  const before = make(() => ({ waitForInput: async () => { throw new Error('context_clear_agent_restarted'); } }));
  before.queueImpl.schedule('State and next tasks');
  await before.runNext();
  assert.deepEqual(before.saved, { followUp: 'State and next tasks', cleared: true });
  // A new process on the same state directory.
  const after = make();
  after.saved = before.saved;
  assert.equal(after.queueImpl.resume(), true);
  assert.equal(await after.runNext(), true);
  assert.deepEqual(after.steps, ['clear', 'save:cleared', 'confirm', 'ready', 'type#1', 'remove']);
  assert.deepEqual(after.transcript, [HANDOVER + 'State and next tasks']);
});

test('a saved handover that is no longer valid is dropped instead of typed', () => {
  const r = make();
  r.saved = { followUp: '\x1b[201~' };
  assert.equal(r.queueImpl.resume(), false);
  assert.equal(r.saved, null);
  assert.equal(r.queue.length, 0);
});

test('invalid handovers cannot inject terminal controls or an empty turn', () => {
  for (const value of ['', '  ', '\x1b[201~', 'x'.repeat(16_001), null]) assert.throws(() => validateFollowUp(value), /invalid_follow_up/);
  assert.equal(validateFollowUp('state\r\nnext\rtask'), 'state\nnext\ntask');
});
