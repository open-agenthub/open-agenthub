'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { validCredential, watchCredential } = require('../../opencode/auth-watcher');

test('only a non-empty map of typed provider entries is a credential', () => {
  const valid = body => validCredential(Buffer.from(JSON.stringify(body)));
  assert.equal(valid({ 'opencode-go': { type: 'api', key: 'k' } }), true);
  assert.equal(valid({ anthropic: { type: 'oauth', refresh: 'r', access: 'a', expires: 1 } }), true);
  // What a logout leaves behind; storing it would replace a working login with nothing.
  assert.equal(valid({}), false);
  assert.equal(valid({ x: { type: 'other' } }), false);
  assert.equal(valid({ x: 'key' }), false);
  assert.equal(valid([]), false);
  assert.equal(validCredential(Buffer.from('not json')), false);
});

test('a new login is uploaded to the OpenCode credential endpoint', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'opencode-auth-'));
  const source = path.join(dir, 'auth.json');
  fs.writeFileSync(source, JSON.stringify({ 'opencode-go': { type: 'api', key: 'k' } }));
  const calls = [];
  const watcher = watchCredential({
    source, callbackUrl: 'http://hub.example.com/internal/sessions/s1/', callbackToken: 'tok',
    expectCreate: true, logger: { info() {}, warn() {} },
    setIntervalImpl: () => null, clearIntervalImpl: () => {},
    fetchImpl: async (url, init) => { calls.push({ url, init }); return { ok: true }; }
  });
  await watcher.ready;
  watcher.stop();
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'http://hub.example.com/internal/sessions/s1/opencode-credentials');
  assert.equal(calls[0].init.method, 'PUT');
  assert.equal(calls[0].init.headers['X-Agent-Token'], 'tok');
});

// A swap by the session agent writes the new file's hash to the baseline file before the file
// itself; the watcher adopts it and neither the old file nor the installed one is uploaded.
test('a baseline written by the session agent suppresses the upload of a swapped-in login', async () => {
  const crypto = require('node:crypto');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'opencode-baseline-'));
  const source = path.join(dir, 'auth.json');
  const baselineFile = path.join(dir, '.agenthub', 'credential-baseline');
  const login = key => Buffer.from(JSON.stringify({ 'opencode-go': { type: 'api', key } }));
  fs.writeFileSync(source, login('restored'));
  const calls = [];
  const watcher = watchCredential({
    source, callbackUrl: 'http://hub.example.com/internal/sessions/s1', callbackToken: 'tok', baselineFile,
    logger: { info() {}, warn() {} }, setIntervalImpl: () => null, clearIntervalImpl: () => {},
    fetchImpl: async (url, init) => { calls.push({ url, init }); return { ok: true }; }
  });
  await watcher.ready;
  const swapped = login('swapped-in-by-the-hub');
  fs.mkdirSync(path.dirname(baselineFile), { recursive: true });
  fs.writeFileSync(baselineFile, crypto.createHash('sha256').update(swapped).digest('hex') + '\n');
  await watcher.poll();
  fs.writeFileSync(source, swapped);
  await watcher.poll();
  assert.equal(calls.length, 0);
  fs.writeFileSync(source, login('logged-in-again'));
  await watcher.poll();
  watcher.stop();
  assert.equal(calls.length, 1);
});
