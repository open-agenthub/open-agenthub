'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const env = () => ({
  AGENTHUB_CALLBACK_URL: 'http://backend/internal/sessions/session-1',
  AGENTHUB_CALLBACK_TOKEN: 'secret-token-never-leak'
});

test('requestPort posts the body with X-Agent-Token', async () => {
  const { NetworkBackendClient } = await import('../../network/client.mjs');
  const calls = [];
  const client = new NetworkBackendClient(env(), async (url, init) => {
    calls.push({ url, init });
    return Response.json({ id: 'req-1' });
  });

  const result = await client.requestPort({
    direction: 'egress', port: 5432, protocol: 'TCP', reason: 'reach Postgres'
  });

  assert.equal(calls[0].url, 'http://backend/internal/sessions/session-1/network/port-requests');
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers['X-Agent-Token'], 'secret-token-never-leak');
  assert.doesNotMatch(calls[0].url, /secret/);
  const body = JSON.parse(calls[0].init.body);
  assert.equal(body.direction, 'egress');
  assert.equal(body.port, 5432);
  assert.equal(body.reason, 'reach Postgres');
  assert.equal(result.id, 'req-1');
});

test('decision, expire, and listPorts hit the expected paths', async () => {
  const { NetworkBackendClient } = await import('../../network/client.mjs');
  const calls = [];
  const client = new NetworkBackendClient(env(), async (url, init) => {
    calls.push({ url, method: init.method });
    if (url.endsWith('/ports')) return Response.json({ ports: [] });
    return Response.json({ decision: 'allow' });
  });

  assert.equal((await client.decision('req-1')).decision, 'allow');
  assert.equal((await client.expire('req-1')).decision, 'allow');
  assert.deepEqual(await client.listPorts(), { ports: [] });

  assert.deepEqual(calls.map(c => [c.method, c.url]), [
    ['GET', 'http://backend/internal/sessions/session-1/network/port-requests/req-1'],
    ['POST', 'http://backend/internal/sessions/session-1/network/port-requests/req-1/expire'],
    ['GET', 'http://backend/internal/sessions/session-1/network/ports']
  ]);
});

test('rejects oversized backend responses with stable code', async () => {
  const { NetworkBackendClient } = await import('../../network/client.mjs');
  const client = new NetworkBackendClient(env(), async () =>
    new Response('x'.repeat(65 * 1024), { status: 200 }));

  await assert.rejects(() => client.listPorts(), /network_backend_response_too_large/);
});

test('maps http errors to stable codes and never includes the token', async () => {
  const { NetworkBackendClient } = await import('../../network/client.mjs');
  const client = new NetworkBackendClient(env(), async () =>
    new Response('nope', { status: 401 }));

  await assert.rejects(async () => {
    try {
      await client.requestPort({ direction: 'egress', port: 5432, reason: 'x' });
    } catch (error) {
      assert.doesNotMatch(String(error), /secret-token-never-leak/);
      throw error;
    }
  }, /network_backend_http_401/);
});

test('requires callback url and token, and a valid url', async () => {
  const { NetworkBackendClient } = await import('../../network/client.mjs');
  assert.throws(() => new NetworkBackendClient({}), /network_backend_not_configured/);
  assert.throws(
    () => new NetworkBackendClient({
      AGENTHUB_CALLBACK_URL: 'not-a-url',
      AGENTHUB_CALLBACK_TOKEN: 't'
    }),
    /network_backend_invalid_url/
  );
});
