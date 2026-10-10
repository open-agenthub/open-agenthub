'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const sessionsDir = path.join(__dirname, '..', '..', 'sessions');

// The same table the backend's SessionExpiryTests and the stdio server's expiry.test.js use.
test('in-pod parseDuration reads a number with a unit and applies the backend limits', async () => {
  const { parseDuration, withExpiry } = await import('../../sessions/expiry.mjs');
  assert.equal(parseDuration('90m'), 5400);
  assert.equal(parseDuration('12h'), 43200);
  assert.equal(parseDuration('3d'), 259200);
  assert.equal(parseDuration('300s'), 300);
  for (const bad of ['', '12', '1.5h', '1w', '4m', '366d']) {
    assert.throws(() => parseDuration(bad), /autodelete_invalid_duration/, `accepted ${JSON.stringify(bad)}`);
  }
  assert.deepEqual(withExpiry({ title: 'x', autoDeleteAfter: '2h' }), { title: 'x', autoDeleteAfterSeconds: 7200 });
  assert.deepEqual(withExpiry({ title: 'x' }), { title: 'x' });
});

test('the in-pod copy of expiry.mjs is the stdio server\'s, byte for byte', () => {
  // Two packages, one grammar: a fix in one must land in the other.
  const inPod = fs.readFileSync(path.join(sessionsDir, 'expiry.mjs'), 'utf8');
  const stdio = fs.readFileSync(path.join(__dirname, '..', '..', '..', 'mcp', 'agenthub', 'expiry.mjs'), 'utf8');
  assert.equal(inPod.replace(/\r\n/g, '\n'), stdio.replace(/\r\n/g, '\n'));
});

test('session_create accepts autoDeleteAfter and converts it before the spawn call', () => {
  const server = fs.readFileSync(path.join(sessionsDir, 'server.mjs'), 'utf8');
  assert.match(server, /autoDeleteAfter: z\.string\(\)\.max\(16\)\.optional\(\)/);
  assert.match(server, /autoDeleteFrom: z\.enum\(\['start', 'lastActivity'\]\)\.optional\(\)/);
  assert.match(server, /client\.create\(withExpiry\(body\)\)/);
  assert.match(server, /'autodelete_invalid_duration'/);
});
