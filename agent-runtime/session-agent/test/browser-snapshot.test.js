'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

test('snapshot refs fail after the page revision changes', async () => {
  const { SnapshotRefs } = await import('../../browser/snapshot.mjs');
  const refs = new SnapshotRefs();
  const ref = refs.remember({ selector: '#submit' });

  refs.advanceRevision();

  assert.throws(() => refs.resolve(ref), /stale/i);
});

test('snapshot refs are opaque and scoped to their revision', async () => {
  const { SnapshotRefs } = await import('../../browser/snapshot.mjs');
  const refs = new SnapshotRefs();
  const ref = refs.remember({ selector: '#email' });

  assert.match(ref, /^e\d+-\d+$/);
  assert.deepEqual(refs.resolve(ref), { selector: '#email' });
});