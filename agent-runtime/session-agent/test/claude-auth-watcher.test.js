'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { watchCredential, validCredential, MAX_CREDENTIAL_BYTES } = require('../../claude/auth-watcher');

async function withServer(statuses, run) {
  const requests = [];
  const server = http.createServer((request, response) => {
    let body = '';
    request.setEncoding('utf8');
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      requests.push({ method: request.method, url: request.url, headers: request.headers, body });
      response.writeHead(statuses.shift() || 204);
      response.end();
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  try {
    await run('http://127.0.0.1:' + address.port + '/internal/sessions/test', requests);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
}

function fixture(value) {
  return JSON.stringify({ claudeAiOauth: { accessToken: value, refreshToken: value + '-r' } });
}

function tempSource(name) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'claude-watcher-'));
  return path.join(directory, name);
}

test('Claude watcher skips restored content and uploads each later valid change once', async () => {
  const source = tempSource('.credentials.json');
  fs.writeFileSync(source, fixture('restored-token'));
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000
    });
    await watcher.ready;
    assert.equal(requests.length, 0);
    fs.writeFileSync(source, fixture('rotated-token'));
    await watcher.poll();
    await watcher.poll();
    fs.writeFileSync(source, fixture('rotated-again'));
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 2);
    assert.deepEqual(requests.map(request => request.url), [
      '/internal/sessions/test/claude-credentials', '/internal/sessions/test/claude-credentials'
    ]);
    assert.equal(requests[0].headers['content-type'], 'application/json');
    assert.equal(requests[0].headers['x-agent-token'], 'synthetic-callback-token');
  });
});

test('a baseline hash suppresses the upload of the credential just restored from the secret', async () => {
  const source = tempSource('.credentials.json');
  const restored = fixture('restored-token');
  fs.writeFileSync(source, restored);
  const baselineHash = crypto.createHash('sha256').update(restored).digest('hex');
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000, baselineHash
    });
    await watcher.ready;
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 0);
  });
});

// The regression this watcher exists for. Anthropic rotates the refresh token on redemption,
// so one the CLI obtained after the last poll lives only in the pod. Dropping it on shutdown
// leaves the stored token already spent, and every later session fails to authenticate.
test('flush uploads a rotation that happened after the last poll', async () => {
  const source = tempSource('.credentials.json');
  fs.writeFileSync(source, fixture('restored-token'));
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000
    });
    await watcher.ready;
    assert.equal(requests.length, 0);
    fs.writeFileSync(source, fixture('rotated-at-the-last-moment'));
    await watcher.flush();
    watcher.stop();
    assert.equal(requests.length, 1);
    assert.equal(JSON.parse(requests[0].body).claudeAiOauth.accessToken, 'rotated-at-the-last-moment');
  });
});

test('flush after stop uploads nothing', async () => {
  const source = tempSource('.credentials.json');
  fs.writeFileSync(source, fixture('restored-token'));
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000
    });
    await watcher.ready;
    watcher.stop();
    fs.writeFileSync(source, fixture('too-late'));
    await watcher.flush();
    assert.equal(requests.length, 0);
  });
});

test('a rejected upload is retried on the next poll', async () => {
  const source = tempSource('.credentials.json');
  fs.writeFileSync(source, fixture('restored-token'));
  await withServer([500], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000,
      logger: { info() {}, warn() {} }
    });
    await watcher.ready;
    fs.writeFileSync(source, fixture('rotated-token'));
    await watcher.poll();
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 2);
    assert.equal(JSON.parse(requests[1].body).claudeAiOauth.accessToken, 'rotated-token');
  });
});

test('validCredential accepts only an object carrying a claudeAiOauth object', () => {
  assert.equal(validCredential(Buffer.from(fixture('t'))), true);
  assert.equal(validCredential(Buffer.from(JSON.stringify({ tokens: {} }))), false);
  assert.equal(validCredential(Buffer.from(JSON.stringify({ claudeAiOauth: null }))), false);
  assert.equal(validCredential(Buffer.from(JSON.stringify({ claudeAiOauth: [] }))), false);
  assert.equal(validCredential(Buffer.from('not json')), false);
  assert.equal(validCredential(Buffer.alloc(0)), false);
  assert.equal(validCredential(Buffer.alloc(MAX_CREDENTIAL_BYTES + 1, 0x20)), false);
});

test('an oversized credential file is never uploaded', async () => {
  const source = tempSource('.credentials.json');
  fs.writeFileSync(source, fixture('restored-token'));
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000
    });
    await watcher.ready;
    fs.writeFileSync(source, ' '.repeat(MAX_CREDENTIAL_BYTES + 1));
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 0);
  });
});

test('a missing credential file is tolerated and picked up once it appears', async () => {
  const source = tempSource('.credentials.json');
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000,
      expectCreate: true
    });
    await watcher.ready;
    assert.equal(requests.length, 0);
    fs.writeFileSync(source, fixture('first-login'));
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 1);
    assert.equal(JSON.parse(requests[0].body).claudeAiOauth.accessToken, 'first-login');
  });
});
