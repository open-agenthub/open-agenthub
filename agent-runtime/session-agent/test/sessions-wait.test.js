'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

test('waitForSession resolves on Succeeded', async () => {
  const { waitForSession } = await import('../../sessions/wait.mjs');
  let calls = 0;
  const get = async id => {
    calls += 1;
    assert.equal(id, 'c1');
    return { id, phase: calls === 1 ? 'Running' : 'Succeeded' };
  };

  const result = await waitForSession(get, 'c1', { intervalMs: 1, timeoutMs: 50 });
  assert.equal(result.phase, 'Succeeded');
  assert.equal(result.timedOut, false);
  assert.equal(result.id, 'c1');
  assert.ok(calls >= 2);
});

test('waitForSession resolves on Failed', async () => {
  const { waitForSession } = await import('../../sessions/wait.mjs');
  const get = async id => ({ id, phase: 'Failed' });
  const result = await waitForSession(get, 'c2', { intervalMs: 1, timeoutMs: 50 });
  assert.equal(result.phase, 'Failed');
  assert.equal(result.timedOut, false);
});

test('waitForSession times out with last phase', async () => {
  const { waitForSession } = await import('../../sessions/wait.mjs');
  const get = async id => ({ id, phase: 'Running' });
  const result = await waitForSession(get, 'c3', { intervalMs: 1, timeoutMs: 20 });
  assert.equal(result.timedOut, true);
  assert.equal(result.phase, 'Running');
  assert.equal(result.id, 'c3');
});
