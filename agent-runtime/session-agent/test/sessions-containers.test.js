'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');

const env = () => ({
  AGENTHUB_CALLBACK_URL: 'http://backend/internal/sessions/session-1',
  AGENTHUB_CALLBACK_TOKEN: 'secret-token-never-leak',
  AGENTHUB_SESSION_ID: 'session-1'
});

test('container calls go to the session\'s containers route with the callback token', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const calls = [];
  const client = new SessionsBackendClient(env(), async (url, init) => {
    calls.push({ url, method: init.method, token: init.headers['X-Agent-Token'], body: init.body });
    return Response.json({ ok: true });
  });

  await client.containerRun({ name: 'db', image: 'postgres:17', ports: [5432] });
  await client.containerList();
  await client.containerLogs('db', { tail: 50, previous: true });
  await client.containerLogs('db');
  await client.containerStop('db');

  assert.deepEqual(calls.map(c => `${c.method} ${c.url}`), [
    'POST http://backend/internal/sessions/session-1/containers',
    'GET http://backend/internal/sessions/session-1/containers',
    'GET http://backend/internal/sessions/session-1/containers/db/logs?tail=50&previous=true',
    'GET http://backend/internal/sessions/session-1/containers/db/logs',
    'DELETE http://backend/internal/sessions/session-1/containers/db'
  ]);
  assert.ok(calls.every(c => c.token === 'secret-token-never-leak'));
  assert.deepEqual(JSON.parse(calls[0].body), { name: 'db', image: 'postgres:17', ports: [5432] });
});

test('a refusal keeps the stable code as message and carries the backend\'s sentence as detail', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const client = new SessionsBackendClient(env(), async () =>
    Response.json({ error: "registry 'other.example.com' is not allowed on this instance" }, { status: 400 }));

  await assert.rejects(() => client.containerRun({ name: 'db', image: 'other.example.com/db' }), error => {
    assert.equal(error.message, 'sessions_backend_http_400');
    assert.equal(error.detail, "registry 'other.example.com' is not allowed on this instance");
    return true;
  });

  const plain = new SessionsBackendClient(env(), async () => new Response('nope', { status: 500 }));
  await assert.rejects(() => plain.containerList(), error => error.message === 'sessions_backend_http_500' && !error.detail);
});

test('codex forwards the containers flag to agenthub_sessions, whose environment it clears', () => {
  const { builtinToml } = require('../../codex/mcp-config.js');
  const toml = builtinToml({ AGENTHUB_SPAWN_MCP_ENABLED: '1' }, '/usr/local/bin/node');
  const block = toml.slice(toml.indexOf('[mcp_servers.agenthub_sessions]'));
  assert.match(block.split('\n\n')[0], /env_vars = \[[^\]]*"AGENTHUB_CONTAINERS_ENABLED"/);
});

// The real server.mjs over a real stdio MCP connection, against a local HTTP stand-in for the
// backend: what the agent's CLI sees, and what reaches the hub.
async function withServer(extraEnv, run) {
  const requests = [];
  const backend = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      requests.push({ method: req.method, url: req.url, token: req.headers['x-agent-token'], body });
      res.setHeader('Content-Type', 'application/json');
      if (req.method === 'POST' && JSON.parse(body).image.startsWith('other.example.com/')) {
        res.statusCode = 400;
        res.end(JSON.stringify({ error: "registry 'other.example.com' is not allowed on this instance; allowed: docker.io." }));
        return;
      }
      if (req.method === 'POST') {
        res.end(JSON.stringify({ name: 'db', image: 'postgres:17', phase: 'Pending', ready: false, host: 'db.ns.svc', ports: [5432] }));
        return;
      }
      res.end(JSON.stringify({ containers: [] }));
    });
  });
  await new Promise(resolve => backend.listen(0, '127.0.0.1', resolve));

  // In the image sessions/ sits next to the session agent's node_modules; mirror that layout.
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sessions-mcp-'));
  fs.cpSync(path.join(__dirname, '..', '..', 'sessions'), path.join(root, 'sessions'), { recursive: true });
  fs.symlinkSync(path.join(__dirname, '..', 'node_modules'), path.join(root, 'node_modules'));

  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { StdioClientTransport } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [path.join(root, 'sessions', 'server.mjs')],
    env: {
      PATH: process.env.PATH,
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${backend.address().port}/internal/sessions/session-1`,
      AGENTHUB_CALLBACK_TOKEN: 'secret-token-never-leak',
      AGENTHUB_SESSION_ID: 'session-1',
      ...extraEnv
    }
  });
  const client = new Client({ name: 'test', version: '1.0.0' });
  try {
    await client.connect(transport);
    await run(client, requests);
  } finally {
    await client.close();
    await new Promise(resolve => backend.close(resolve));
    fs.rmSync(root, { recursive: true, force: true });
  }
}

test('the container tools exist only when the hub enables them', async () => {
  await withServer({}, async client => {
    const names = (await client.listTools()).tools.map(t => t.name);
    assert.ok(names.includes('session_create'));
    assert.ok(!names.some(n => n.startsWith('container_')));
  });
  await withServer({ AGENTHUB_CONTAINERS_ENABLED: '1' }, async client => {
    const names = (await client.listTools()).tools.map(t => t.name);
    for (const name of ['container_run', 'container_list', 'container_logs', 'container_stop'])
      assert.ok(names.includes(name), name);
  });
});

test('container_run forwards the request and returns the hub\'s refusal reason to the agent', async () => {
  await withServer({ AGENTHUB_CONTAINERS_ENABLED: '1' }, async (client, requests) => {
    const ok = await client.callTool({
      name: 'container_run',
      arguments: { name: 'db', image: 'postgres:17', ports: [5432], env: { POSTGRES_PASSWORD: 'x' },
        volumes: [{ mountPath: '/var/lib/postgresql/data', sizeLimit: '1Gi' }] }
    });
    assert.ok(!ok.isError);
    assert.equal(JSON.parse(ok.content[0].text).host, 'db.ns.svc');
    assert.equal(requests[0].method, 'POST');
    assert.equal(requests[0].url, '/internal/sessions/session-1/containers');
    assert.equal(requests[0].token, 'secret-token-never-leak');
    assert.deepEqual(JSON.parse(requests[0].body), {
      name: 'db', image: 'postgres:17', ports: [5432], env: { POSTGRES_PASSWORD: 'x' },
      volumes: [{ mountPath: '/var/lib/postgresql/data', sizeLimit: '1Gi' }]
    });

    const refused = await client.callTool({ name: 'container_run', arguments: { name: 'db', image: 'other.example.com/db' } });
    assert.ok(refused.isError);
    assert.deepEqual(JSON.parse(refused.content[0].text), {
      error: 'sessions_backend_http_400',
      message: "registry 'other.example.com' is not allowed on this instance; allowed: docker.io."
    });

    // zod refuses a name the hub would refuse too, before any request is made.
    const badName = await client.callTool({ name: 'container_run', arguments: { name: 'DB', image: 'postgres:17' } });
    assert.ok(badName.isError);
    assert.equal(requests.length, 2);

    await client.callTool({ name: 'container_logs', arguments: { name: 'db', tail: 20 } });
    await client.callTool({ name: 'container_stop', arguments: { name: 'db' } });
    assert.deepEqual(requests.slice(2).map(r => `${r.method} ${r.url}`), [
      'GET /internal/sessions/session-1/containers/db/logs?tail=20',
      'DELETE /internal/sessions/session-1/containers/db'
    ]);
  });
});
