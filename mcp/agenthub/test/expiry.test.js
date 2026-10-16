import test from 'node:test';
import assert from 'node:assert/strict';
import { parseDuration, withExpiry } from '../expiry.mjs';

// The same table the backend's SessionExpiryTests use, so the three parsers agree.
test('parseDuration reads a number with a unit and applies the backend limits', () => {
  assert.equal(parseDuration('90m'), 5400);
  assert.equal(parseDuration('12h'), 43200);
  assert.equal(parseDuration('3d'), 259200);
  assert.equal(parseDuration('300s'), 300);
  assert.equal(parseDuration(' 2H '), 7200);
  assert.equal(parseDuration('365d'), 31536000);
  for (const bad of ['', undefined, '12', '1.5h', '2 days', '1w', '4m', '366d']) {
    assert.throws(() => parseDuration(bad), /autodelete_invalid_duration/, `accepted ${JSON.stringify(bad)}`);
  }
});

test('withExpiry converts the tool field to the REST field and leaves the rest alone', () => {
  assert.deepEqual(withExpiry({ title: 'x', autoDeleteAfter: '12h', autoDeleteFrom: 'start' }),
    { title: 'x', autoDeleteAfterSeconds: 43200, autoDeleteFrom: 'start' });
  assert.deepEqual(withExpiry({ title: 'x' }), { title: 'x' });
  assert.deepEqual(withExpiry({ title: 'x', autoDeleteAfter: '' }), { title: 'x' });
  assert.throws(() => withExpiry({ autoDeleteAfter: '12' }), /autodelete_invalid_duration/);
});
