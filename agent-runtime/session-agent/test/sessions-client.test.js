'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const env = () => ({
  AGENTHUB_CALLBACK_URL: 'http://backend/internal/sessions/session-1',
  AGENTHUB_CALLBACK_TOKEN: 'secret-token-never-leak',
  AGENTHUB_SESSION_ID: 'session-1'
});

const sessionInfo = (overrides = {}) => ({
  id: 'child-1',
  title: 'Worker',
  owner: 'user-1',
  parentSessionId: 'session-1',
  mode: 'Autonomous',
  phase: 'Running',
  agent: 'Claude',
  authMode: 'Subscription',
  ...overrides
});

test('create posts spawn with X-Agent-Token and defaults mode Autonomous', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const calls = [];
  const client = new SessionsBackendClient(env(), async (url, init) => {
    calls.push({ url, init });
    return Response.json(sessionInfo());
  });

  const result = await client.create({ title: 'Worker', prompt: 'do work' });

  assert.equal(calls[0].url, 'http://backend/internal/sessions/session-1/spawn');
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers['X-Agent-Token'], 'secret-token-never-leak');
  assert.doesNotMatch(calls[0].url, /secret/);
  const body = JSON.parse(calls[0].init.body);
  assert.equal(body.mode, 'Autonomous');
  assert.equal(body.title, 'Worker');
  assert.equal(body.prompt, 'do work');
  assert.equal(body.parentSessionId, 'session-1');
  assert.equal(result.id, 'child-1');
});

test('get and delete use peer path; listChildren uses children path', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const calls = [];
  const client = new SessionsBackendClient(env(), async (url, init) => {
    calls.push({ url, method: init.method });
    if (init.method === 'DELETE') return new Response(null, { status: 204 });
    if (url.endsWith('/children')) return Response.json([sessionInfo()]);
    return Response.json(sessionInfo({ phase: 'Succeeded' }));
  });

  assert.equal((await client.get('child-1')).phase, 'Succeeded');
  assert.equal((await client.listChildren())[0].id, 'child-1');
  assert.deepEqual(await client.delete('child-1'), { deleted: true });

  assert.deepEqual(calls.map(c => [c.method, c.url]), [
    ['GET', 'http://backend/internal/sessions/session-1/peer/child-1'],
    ['GET', 'http://backend/internal/sessions/session-1/children'],
    ['DELETE', 'http://backend/internal/sessions/session-1/peer/child-1']
  ]);
});

test('rejects oversized backend responses with stable code', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const client = new SessionsBackendClient(env(), async () =>
    new Response('x'.repeat(65 * 1024), { status: 200 }));

  await assert.rejects(() => client.get('child-1'), /sessions_backend_response_too_large/);
});

test('maps http errors to stable codes and never includes the token', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const client = new SessionsBackendClient(env(), async () =>
    new Response('nope', { status: 429 }));

  await assert.rejects(async () => {
    try {
      await client.create({ title: 'x', prompt: 'y' });
    } catch (error) {
      assert.doesNotMatch(String(error), /secret-token-never-leak/);
      throw error;
    }
  }, /sessions_backend_http_429/);
});

test('requires callback url, token, and session id', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  assert.throws(() => new SessionsBackendClient({}), /sessions_backend_not_configured/);
  assert.throws(
    () => new SessionsBackendClient({
      AGENTHUB_CALLBACK_URL: 'not-a-url',
      AGENTHUB_CALLBACK_TOKEN: 't',
      AGENTHUB_SESSION_ID: 's'
    }),
    /sessions_backend_invalid_url/
  );
});
