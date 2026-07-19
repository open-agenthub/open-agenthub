'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { watchCredential, MAX_CREDENTIAL_BYTES } = require('../../codex/auth-watcher');

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

function fixture(value) { return JSON.stringify({ tokens: { access_token: value } }); }

test('Codex watcher skips restored content and uploads each later valid change once', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-watcher-'));
  const source = path.join(directory, 'auth.json');
  fs.writeFileSync(source, fixture('restored-token'));
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000
    });
    await watcher.ready;
    assert.equal(requests.length, 0);
    fs.writeFileSync(source, fixture('created-token'));
    await watcher.poll();
    await watcher.poll();
    fs.writeFileSync(source, fixture('refreshed-token'));
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 2);
    assert.deepEqual(requests.map(request => request.method), ['PUT', 'PUT']);
    assert.deepEqual(requests.map(request => request.url), [
      '/internal/sessions/test/codex-credentials', '/internal/sessions/test/codex-credentials'
    ]);
    assert.equal(requests[0].headers['content-type'], 'application/json');
    assert.equal(requests[0].headers['x-agent-token'], 'synthetic-callback-token');
  });
});

test('Codex watcher uploads creation and retries unchanged content after failure', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-watcher-'));
  const source = path.join(directory, 'auth.json');
  const logs = [];
  await withServer([500, 204], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'callback-secret-never-log', intervalMs: 60_000,
      logger: { warn: message => logs.push(String(message)), info: message => logs.push(String(message)) }
    });
    await watcher.ready;
    fs.writeFileSync(source, fixture('credential-secret-never-log'));
    await watcher.poll();
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 2);
    assert.equal(requests[0].body, requests[1].body);
    assert.doesNotMatch(logs.join('\n'), /credential-secret-never-log|callback-secret-never-log/);
  });
});

test('Codex watcher uploads login created before its first poll when creation was expected', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-watcher-'));
  const source = path.join(directory, 'auth.json');
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source,
      callbackUrl,
      callbackToken: 'synthetic-callback-token',
      intervalMs: 60_000,
      expectCreate: true
    });
    try {
      fs.writeFileSync(source, fixture('created-before-first-poll'));
      await watcher.ready;
      await watcher.poll();
      assert.equal(requests.length, 1);
      assert.equal(requests[0].body, fixture('created-before-first-poll'));
    } finally {
      watcher.stop();
    }
  });
});

test('Codex watcher rejects invalid shape and content over backend 64 KiB limit', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-watcher-'));
  const source = path.join(directory, 'auth.json');
  await withServer([], async (callbackUrl, requests) => {
    const watcher = watchCredential({
      source, callbackUrl, callbackToken: 'synthetic-callback-token', intervalMs: 60_000
    });
    await watcher.ready;
    fs.writeFileSync(source, '{"tokens":');
    await watcher.poll();
    fs.writeFileSync(source, JSON.stringify({ unrelated: {} }));
    await watcher.poll();
    fs.writeFileSync(source, Buffer.alloc(MAX_CREDENTIAL_BYTES + 1, 0x20));
    await watcher.poll();
    assert.equal(requests.length, 0);
    fs.writeFileSync(source, fixture('valid'));
    await watcher.poll();
    watcher.stop();
    assert.equal(requests.length, 1);
  });
});
