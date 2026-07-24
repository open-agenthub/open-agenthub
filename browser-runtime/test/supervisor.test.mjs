import assert from 'node:assert/strict';
import test from 'node:test';

import {
  BrowserSupervisor,
  COOKIE_STATE_VERSION,
  decodeCookieState,
  encodeCookieState,
} from '../supervisor.mjs';

const validCookie = {
  name: 'session', value: 'secret', domain: '.example.test', path: '/',
  expires: 2_000_000_000, httpOnly: true, secure: true, sameSite: 'Lax',
};

test('cookie state accepts only valid browser cookies and filters unknown fields', () => {
  const raw = JSON.stringify({ version: COOKIE_STATE_VERSION, cookies: [
    { ...validCookie, ignored: 'value' },
    { ...validCookie, name: '', value: 'invalid' },
    { ...validCookie, sameSite: 'Invalid' }, null,
  ] });
  assert.deepEqual(decodeCookieState(Buffer.from(raw), 1_048_576), [{ ...validCookie }]);
});

test('cookie state rejects payloads over the configured cap', () => {
  assert.deepEqual(decodeCookieState(Buffer.alloc(1_048_577, 0x20), 1_048_576), []);
  assert.throws(() => encodeCookieState([{ ...validCookie, value: 'x'.repeat(1_048_576) }], 1_048_576), /cookie state exceeds/i);
});

test('corrupt restore state is ignored without interrupting startup', async () => {
  const context = fakeContext();
  const requests = [];
  const supervisor = makeSupervisor({ context, fetch: async (url, options = {}) => {
    requests.push({ url, options });
    if (url.endsWith('/state-urls')) return jsonResponse({ getUrl: 'https://store.test/get', putUrl: 'https://store.test/put' });
    return new Response('{broken', { status: 200 });
  } });
  await supervisor.restore();
  assert.equal(context.addCookiesCalls.length, 0);
  assert.equal(requests[0].options.headers['X-Browser-Token'], 'lease-secret');
  assert.ok(!requests[0].url.includes('lease-secret'));
  assert.equal(requests[1].options.headers, undefined);
});

test('checkpoint places lease token only on callback and uploads no credentials', async () => {
  const context = fakeContext([validCookie]);
  const requests = [];
  const supervisor = makeSupervisor({ context, fetch: async (url, options = {}) => {
    requests.push({ url, options });
    if (url.endsWith('/state-urls')) return jsonResponse({ getUrl: 'https://store.test/get', putUrl: 'https://store.test/put' });
    return new Response(null, { status: 200 });
  } });
  assert.equal(await supervisor.checkpoint(), true);
  assert.equal(requests[0].options.headers['X-Browser-Token'], 'lease-secret');
  assert.ok(!requests[0].url.includes('lease-secret'));
  assert.equal(requests[1].url, 'https://store.test/put');
  assert.equal(requests[1].options.method, 'PUT');
  assert.equal(requests[1].options.headers['Content-Type'], 'application/json');
  assert.equal(requests[1].options.headers['X-Browser-Token'], undefined);
  assert.deepEqual(JSON.parse(requests[1].options.body), { version: COOKIE_STATE_VERSION, cookies: [validCookie] });
});

test('shutdown stops periodic work, checkpoints once, disconnects, and is idempotent', async () => {
  const events = [];
  const supervisor = makeSupervisor({ context: fakeContext(), fetch: async () => new Response(null, { status: 404 }) });
  supervisor.timer = { fake: true };
  supervisor.clearInterval = timer => events.push(['clear', timer]);
  supervisor.checkpoint = async () => { events.push(['checkpoint']); return true; };
  supervisor.browser = { close: async () => events.push(['close']) };
  await supervisor.shutdown();
  await supervisor.shutdown();
  assert.deepEqual(events, [['clear', { fake: true }], ['checkpoint'], ['close']]);
});

function makeSupervisor(overrides = {}) {
  return new BrowserSupervisor({ callbackUrl: 'http://backend.test/internal/browser/lease-1/state-urls', token: 'lease-secret', maxBytes: 1_048_576, checkpointSeconds: 60, fetch: globalThis.fetch, ...overrides });
}
function fakeContext(cookies = []) {
  return { addCookiesCalls: [], async addCookies(value) { this.addCookiesCalls.push(value); }, async cookies() { return cookies; } };
}
function jsonResponse(value) {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

