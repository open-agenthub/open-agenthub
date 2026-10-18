'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { createContextClear, validateFollowUp } = require('../../common/context-clear');

test('clear waits for the shared safe point and confirms fresh context before its first message', async () => {
  let release;
  let confirm;
  const writes = [];
  const queue = createContextClear({
    scheduleAtSafePoint: action => { release = action; },
    clear: async () => { writes.push('/clear'); return 'old'; },
    waitForClear: previous => { assert.equal(previous, 'old'); return new Promise(resolve => { confirm = resolve; }); },
    deliver: async text => { writes.push(text); }
  });
  assert.deepEqual(queue.schedule('/plan is the next topic. State: tests passed.'), { scheduled: true });
  assert.deepEqual(writes, []);
  assert.equal(queue.pending, true);
  assert.throws(() => queue.schedule('duplicate'), /context_clear_pending/);
  const running = release();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(writes, ['/clear']);
  assert.equal(queue.executing, true);
  confirm();
  await running;
  assert.deepEqual(writes, ['/clear', '[AgentHub context handover]\n/plan is the next topic. State: tests passed.']);
  assert.equal(queue.pending, false);
});

test('failed clears never deliver the handover into the old context and report the failure', async () => {
  let release;
  const errors = [];
  const queue = createContextClear({ scheduleAtSafePoint: action => { release = action; },
    clear: async () => { throw new Error('clear_failed'); }, waitForClear: async () => {},
    deliver: async () => assert.fail('handover reached old context'), onError: error => errors.push(error.message) });
  queue.schedule('State and next tasks');
  await release();
  assert.deepEqual(errors, ['clear_failed']);
  assert.equal(queue.pending, false);
});

test('invalid handovers cannot inject terminal controls or an empty turn', () => {
  for (const value of ['', '  ', '\x1b[201~', 'x'.repeat(16_001), null]) assert.throws(() => validateFollowUp(value), /invalid_follow_up/);
  assert.equal(validateFollowUp('state\r\nnext\rtask'), 'state\nnext\ntask');
});
