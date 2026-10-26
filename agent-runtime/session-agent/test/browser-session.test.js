'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');

function fakeBrowser({ contexts = [fakeContext()], probe = async () => ({}) } = {}) {
  const browser = new EventEmitter();
  Object.assign(browser, {
    connected: true,
    closed: 0,
    isConnected() { return this.connected; },
    contexts: () => contexts,
    async close() { this.closed += 1; this.connected = false; },
    async newBrowserCDPSession() { return { send: probe, async detach() {} }; },
    async newContext() { throw new Error('newContext must not be called'); }
  });
  return browser;
}
function fakeContext() {
  const page = { isClosed: () => false, on() {}, mainFrame() {}, async bringToFront() {} };
  return { pages: () => [page], async newPage() { return page; } };
}
function backendAt(endpoints) {
  return { calls: 0, async start() { return { cdpEndpoint: endpoints[Math.min(this.calls++, endpoints.length - 1)] }; } };
}

test('browser_start reconnects when the backend hands out a new endpoint, even if the old one looks connected', async () => {
  const { BrowserSession } = await import('../../browser/session.mjs');
  const connected = [];
  const browsers = [fakeBrowser(), fakeBrowser()];
  const session = new BrowserSession(backendAt(['http://10.0.0.1:9222', 'http://10.0.0.2:9222']), {},
    async (_, endpoint) => { connected.push(endpoint); return browsers[connected.length - 1]; });

  await session.start();
  await session.start();

  assert.deepEqual(connected, ['http://10.0.0.1:9222', 'http://10.0.0.2:9222']);
  assert.equal(browsers[0].closed, 1);
  assert.equal(session.browser, browsers[1]);
});

test('browser_start keeps a live connection to the same endpoint', async () => {
  const { BrowserSession } = await import('../../browser/session.mjs');
  let connects = 0;
  const session = new BrowserSession(backendAt(['http://10.0.0.1:9222']), {},
    async () => { connects += 1; return fakeBrowser(); });

  await Promise.all([session.start(), session.start()]);
  await session.start();

  assert.equal(connects, 1);
});

test('without a default context the session fails instead of opening a context of its own', async () => {
  const { BrowserSession } = await import('../../browser/session.mjs');
  const browser = fakeBrowser({ contexts: [] });
  const session = new BrowserSession(backendAt(['http://10.0.0.1:9222']), {}, async () => browser);

  await assert.rejects(session.start(), /browser_context_unavailable/);
  assert.equal(browser.closed, 1);
  assert.equal(session.browser, null);
});

test('a failed action drops a connection whose browser no longer answers, so the next call reconnects', async () => {
  const { BrowserSession } = await import('../../browser/session.mjs');
  const silent = fakeBrowser({ probe: () => new Promise(() => {}) });
  const session = new BrowserSession(backendAt(['http://10.0.0.1:9222']), {}, async () => silent, 50);
  await session.start();

  assert.equal(await session.recover(), true);
  assert.equal(session.browser, null);
  assert.equal(silent.closed, 1);
});

test('a failed action keeps a connection whose browser still answers', async () => {
  const { BrowserSession } = await import('../../browser/session.mjs');
  const browser = fakeBrowser();
  const session = new BrowserSession(backendAt(['http://10.0.0.1:9222']), {}, async () => browser, 50);
  await session.start();

  assert.equal(await session.recover(), false);
  assert.equal(session.browser, browser);
});

test('a disconnect forgets the connection', async () => {
  const { BrowserSession } = await import('../../browser/session.mjs');
  const browser = fakeBrowser();
  const session = new BrowserSession(backendAt(['http://10.0.0.1:9222']), {}, async () => browser);
  await session.start();

  browser.emit('disconnected');

  assert.equal(session.browser, null);
});
