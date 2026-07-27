'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const env = () => ({
  AGENTHUB_CALLBACK_URL: 'http://backend/internal/sessions/session-1',
  AGENTHUB_CALLBACK_TOKEN: 'secret'
});

const connection = () => ({
  browser: { phase: 'Running', screenWidth: 1440, screenHeight: 900 },
  cdpEndpoint: 'http://10.0.0.9:9222',
  podIp: '10.0.0.9'
});

test('lifecycle sends the session token only in X-Agent-Token', async () => {
  const { BrowserBackendClient } = await import('../../browser/client.mjs');
  const calls = [];
  const client = new BrowserBackendClient(env(), async (url, init) => {
    calls.push({ url, init });
    return Response.json(connection());
  });

  await client.start();

  assert.equal(calls[0].init.headers['X-Agent-Token'], 'secret');
  assert.doesNotMatch(calls[0].url, /secret/);
  assert.equal(calls[0].init.method, 'POST');
});

test('lifecycle rejects oversized backend responses', async () => {
  const { BrowserBackendClient } = await import('../../browser/client.mjs');
  const client = new BrowserBackendClient(env(), async () =>
    new Response('x'.repeat(65 * 1024), { status: 200 }));

  await assert.rejects(() => client.status(), /response_too_large/);
});