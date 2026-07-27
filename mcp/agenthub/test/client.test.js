import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';

const TOKEN = 'oah_test-token-never-leak';

function sessionInfo(overrides = {}) {
  return {
    id: 'sess-1',
    title: 'Worker',
    owner: 'user-1',
    parentSessionId: null,
    mode: 'Autonomous',
    phase: 'Running',
    agent: 'Claude',
    authMode: 'Subscription',
    ...overrides
  };
}

async function withFakeServer(handler, fn) {
  const server = http.createServer((req, res) => {
    const chunks = [];
    req.on('data', c => chunks.push(c));
    req.on('end', () => {
      const body = Buffer.concat(chunks).toString('utf8');
      Promise.resolve(handler(req, body, res)).catch(err => {
        res.writeHead(500, { 'Content-Type': 'text/plain' });
        res.end(String(err));
      });
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await fn(baseUrl);
  } finally {
    await new Promise((resolve, reject) => server.close(err => (err ? reject(err) : resolve())));
  }
}

function json(res, status, value) {
  const body = value === undefined ? '' : JSON.stringify(value);
  res.writeHead(status, {
    'Content-Type': 'application/json',
    ...(body ? { 'Content-Length': Buffer.byteLength(body) } : {})
  });
  res.end(body);
}

test('create posts /api/remote/sessions with Bearer token and defaults mode Autonomous', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const calls = [];

  await withFakeServer((req, body, res) => {
    calls.push({
      method: req.method,
      url: req.url,
      authorization: req.headers.authorization,
      body
    });
    json(res, 200, sessionInfo());
  }, async baseUrl => {
    const client = new AgentHubClient({
      AGENTHUB_URL: baseUrl,
      AGENTHUB_TOKEN: TOKEN
    });

    const result = await client.create({ title: 'Worker', prompt: 'do work' });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].method, 'POST');
    assert.equal(calls[0].url, '/api/remote/sessions');
    assert.equal(calls[0].authorization, `Bearer ${TOKEN}`);
    const payload = JSON.parse(calls[0].body);
    assert.equal(payload.mode, 'Autonomous');
    assert.equal(payload.title, 'Worker');
    assert.equal(payload.prompt, 'do work');
    assert.equal(result.id, 'sess-1');
  });
});

test('get list and delete hit remote sessions routes with Bearer auth', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const calls = [];

  await withFakeServer((req, body, res) => {
    calls.push({
      method: req.method,
      url: req.url,
      authorization: req.headers.authorization
    });
    if (req.method === 'DELETE') {
      res.writeHead(204);
      res.end();
      return;
    }
    if (req.method === 'GET' && req.url === '/api/remote/sessions') {
      json(res, 200, [sessionInfo(), sessionInfo({ id: 'sess-2', phase: 'Succeeded', parentSessionId: 'sess-1' })]);
      return;
    }
    json(res, 200, sessionInfo({ phase: 'Succeeded' }));
  }, async baseUrl => {
    const client = new AgentHubClient({
      AGENTHUB_URL: baseUrl,
      AGENTHUB_TOKEN: TOKEN
    });

    assert.equal((await client.get('sess-1')).phase, 'Succeeded');
    const listed = await client.list();
    assert.equal(listed.length, 2);
    assert.deepEqual(await client.delete('sess-1'), { deleted: true });

    assert.deepEqual(calls.map(c => [c.method, c.url, c.authorization]), [
      ['GET', '/api/remote/sessions/sess-1', `Bearer ${TOKEN}`],
      ['GET', '/api/remote/sessions', `Bearer ${TOKEN}`],
      ['DELETE', '/api/remote/sessions/sess-1', `Bearer ${TOKEN}`]
    ]);
  });
});

test('list filters by parentSessionId and phase client-side', async () => {
  const { AgentHubClient } = await import('../client.mjs');

  await withFakeServer((req, body, res) => {
    json(res, 200, [
      sessionInfo({ id: 'a', phase: 'Running', parentSessionId: 'root' }),
      sessionInfo({ id: 'b', phase: 'Succeeded', parentSessionId: 'root' }),
      sessionInfo({ id: 'c', phase: 'Running', parentSessionId: null })
    ]);
  }, async baseUrl => {
    const client = new AgentHubClient({
      AGENTHUB_URL: baseUrl,
      AGENTHUB_TOKEN: TOKEN
    });

    const filtered = await client.list({ parentSessionId: 'root', phase: 'Running' });
    assert.deepEqual(filtered.map(s => s.id), ['a']);
  });
});

test('rejects oversized backend responses with stable code', async () => {
  const { AgentHubClient } = await import('../client.mjs');

  await withFakeServer((req, body, res) => {
    const payload = 'x'.repeat(65 * 1024);
    res.writeHead(200, {
      'Content-Type': 'application/json',
      'Content-Length': Buffer.byteLength(payload)
    });
    res.end(payload);
  }, async baseUrl => {
    const client = new AgentHubClient({
      AGENTHUB_URL: baseUrl,
      AGENTHUB_TOKEN: TOKEN
    });
    await assert.rejects(() => client.get('sess-1'), /agenthub_response_too_large/);
  });
});

test('maps http errors to stable codes and never includes the token', async () => {
  const { AgentHubClient } = await import('../client.mjs');

  await withFakeServer((req, body, res) => {
    res.writeHead(429, { 'Content-Type': 'text/plain' });
    res.end('too many');
  }, async baseUrl => {
    const client = new AgentHubClient({
      AGENTHUB_URL: baseUrl,
      AGENTHUB_TOKEN: TOKEN
    });

    await assert.rejects(async () => {
      try {
        await client.create({ title: 'x', prompt: 'y' });
      } catch (error) {
        assert.doesNotMatch(String(error), /oah_test-token-never-leak/);
        throw error;
      }
    }, /agenthub_http_429/);
  });
});

test('requires AGENTHUB_URL and AGENTHUB_TOKEN', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  assert.throws(() => new AgentHubClient({}), /agenthub_not_configured/);
  assert.throws(
    () => new AgentHubClient({ AGENTHUB_URL: 'not-a-url', AGENTHUB_TOKEN: TOKEN }),
    /agenthub_invalid_url/
  );
});
