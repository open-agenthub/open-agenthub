'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

test('resolves as soon as the decision turns allow', async () => {
  const { waitForDecision } = await import('../../network/poll.mjs');
  const answers = [{ decision: 'pending' }, { decision: 'pending' }, { decision: 'allow' }];
  let expired = false;

  const result = await waitForDecision(
    async () => answers.shift(),
    async () => { expired = true; return { decision: 'expired' }; },
    { intervalMs: 1, timeoutMs: 1_000 });

  assert.deepEqual(result, { decision: 'allow', timedOut: false });
  assert.equal(expired, false);
});

test('passes deny through without expiring', async () => {
  const { waitForDecision } = await import('../../network/poll.mjs');
  let expired = false;

  const result = await waitForDecision(
    async () => ({ decision: 'deny' }),
    async () => { expired = true; return { decision: 'expired' }; },
    { intervalMs: 1, timeoutMs: 1_000 });

  assert.deepEqual(result, { decision: 'deny', timedOut: false });
  assert.equal(expired, false);
});

test('expires on timeout and reports timedOut', async () => {
  const { waitForDecision } = await import('../../network/poll.mjs');
  let expired = false;

  const result = await waitForDecision(
    async () => ({ decision: 'pending' }),
    async () => { expired = true; return { decision: 'expired' }; },
    { intervalMs: 1, timeoutMs: 5 });

  assert.equal(expired, true);
  assert.deepEqual(result, { decision: 'expired', timedOut: true });
});

test('honors an approval that won the race against the expiry', async () => {
  const { waitForDecision } = await import('../../network/poll.mjs');

  const result = await waitForDecision(
    async () => ({ decision: 'pending' }),
    async () => ({ decision: 'allow' }),
    { intervalMs: 1, timeoutMs: 5 });

  assert.deepEqual(result, { decision: 'allow', timedOut: false });
});
