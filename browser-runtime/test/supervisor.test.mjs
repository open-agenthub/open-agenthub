import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';
import { Readable } from 'node:stream';

import {
  BrowserSupervisor,
  COOKIE_STATE_VERSION,
  decodeCookieState,
  encodeCookieState,
} from '../supervisor.mjs';
import * as supervisorModule from '../supervisor.mjs';

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

test('viewport validation accepts supported integer dimensions and rejects invalid bounds', () => {
  assert.deepEqual(supervisorModule.validateViewport({ width: 800, height: 600 }), { width: 800, height: 600 });
  assert.deepEqual(supervisorModule.validateViewport({ width: 480, height: 320 }), { width: 480, height: 320 });
  assert.deepEqual(supervisorModule.validateViewport({ width: 2560, height: 1600 }), { width: 2560, height: 1600 });
  for (const value of [
    { width: 479, height: 600 },
    { width: 800, height: 319 },
    { width: 2561, height: 600 },
    { width: 800, height: 1601 },
    { width: 800.5, height: 600 },
  ]) assert.throws(() => supervisorModule.validateViewport(value), /viewport/i);
});

test('viewport resize changes X display before matching the Chromium window', async () => {
  const events = [];
  const cdp = {
    async send(method, params) {
      events.push(['cdp', method, params]);
      return method === 'Browser.getWindowForTarget' ? { windowId: 7 } : {};
    },
    async detach() { events.push(['detach']); },
  };
  const context = {
    pages: () => [{ id: 'page-1' }],
    async newCDPSession() { return cdp; },
  };
  const supervisor = makeSupervisor({
    context,
    execFile: async (command, args) => { events.push([command, args]); },
  });

  await supervisor.resizeViewport(800, 600);

  assert.deepEqual(events, [
    ['xrandr', ['--display', ':99', '--fb', '800x600']],
    ['cdp', 'Browser.getWindowForTarget', undefined],
    ['cdp', 'Browser.setWindowBounds', {
      windowId: 7,
      bounds: { left: 0, top: 0, width: 800, height: 600, windowState: 'normal' },
    }],
    ['detach'],
  ]);
});

test('viewport resize accepts an XRandR error only when the framebuffer already matches', async () => {
  const calls = [];
  const context = {
    pages: () => [{}],
    async newCDPSession() {
      return {
        async send(method) {
          calls.push(method);
          return method === 'Browser.getWindowForTarget' ? { windowId: 4 } : {};
        },
        async detach() {},
      };
    },
  };
  const supervisor = makeSupervisor({
    context,
    execFile: async (command, args) => {
      if (args.includes('--fb')) throw new Error('output mode is larger than framebuffer');
      assert.deepEqual(args, ['--display', ':99', '--current']);
      return { stdout: 'Screen 0: minimum 1 x 1, current 800 x 700, maximum 1440 x 900' };
    },
  });

  await supervisor.resizeViewport(800, 700);

  assert.deepEqual(calls, ['Browser.getWindowForTarget', 'Browser.setWindowBounds']);
});

test('viewport resize retries Chromium while it processes the XRandR change', async () => {
  const sleeps = [];
  let boundsAttempts = 0;
  let sessionCreates = 0;
  const context = {
    pages: () => [{}],
    async newCDPSession() {
      sessionCreates += 1;
      if (sessionCreates <= 6) throw new Error('X server resize is still settling');
      return {
        async send(method) {
          if (method === 'Browser.getWindowForTarget') return { windowId: sessionCreates };
          boundsAttempts += 1;
          return {};
        },
        async detach() {},
      };
    },
  };
  const supervisor = makeSupervisor({
    context,
    execFile: async () => {},
    sleep: async milliseconds => { sleeps.push(milliseconds); },
  });

  await supervisor.resizeViewport(800, 600);

  assert.equal(boundsAttempts, 1);
  assert.equal(sessionCreates, 7);
  assert.deepEqual(sleeps, [100, 100, 100, 100, 100, 100]);
});
test('failed X resize skips CDP and does not poison a later viewport resize', async () => {
  const events = [];
  let attempt = 0;
  const context = {
    pages: () => [{}],
    async newCDPSession() {
      return {
        async send(method) {
          events.push(method);
          return method === 'Browser.getWindowForTarget' ? { windowId: 3 } : {};
        },
        async detach() {},
      };
    },
  };
  const supervisor = makeSupervisor({
    context,
    execFile: async () => {
      attempt += 1;
      if (attempt === 1) throw new Error('xrandr failed');
    },
  });

  await assert.rejects(() => supervisor.resizeViewport(800, 600), /xrandr failed/);
  assert.deepEqual(events, []);
  await supervisor.resizeViewport(900, 700);
  assert.deepEqual(events, ['Browser.getWindowForTarget', 'Browser.setWindowBounds']);
});

test('viewport HTTP handler accepts valid dimensions and rejects malformed input', async () => {
  const calls = [];
  const supervisor = makeSupervisor();
  supervisor.resizeViewport = async (width, height) => { calls.push({ width, height }); };

  assert.equal((await invokeViewport(supervisor, JSON.stringify({ width: 800, height: 600 }))).status, 204);
  assert.deepEqual(calls, [{ width: 800, height: 600 }]);
  assert.equal((await invokeViewport(supervisor, JSON.stringify({ width: 200, height: 600 }))).status, 400);
  assert.equal((await invokeViewport(supervisor, '{broken')).status, 400);
  assert.equal((await invokeViewport(supervisor, 'x'.repeat(1025))).status, 400);
});

test('viewport HTTP handler reports runtime failures without terminating the server', async () => {
  const supervisor = makeSupervisor();
  let calls = 0;
  supervisor.resizeViewport = async () => {
    calls += 1;
    if (calls === 1) throw new Error('resize failed');
  };

  assert.equal((await invokeViewport(supervisor, JSON.stringify({ width: 800, height: 600 }))).status, 500);
  assert.equal((await invokeViewport(supervisor, JSON.stringify({ width: 900, height: 700 }))).status, 204);
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

test('checkpoint calls are serialized so the final cookie state wins', async () => {
  let cookieRead = 0;
  let releaseFirst;
  const firstPut = new Promise(resolve => { releaseFirst = resolve; });
  const uploads = [];
  const context = { async cookies() {
    cookieRead += 1;
    return [{ ...validCookie, value: cookieRead === 1 ? 'old' : 'new' }];
  } };
  const supervisor = makeSupervisor({ context, fetch: async (url, options = {}) => {
    if (url.endsWith('/state-urls')) return jsonResponse({ getUrl: '', putUrl: 'https://store.test/put' });
    uploads.push(JSON.parse(options.body).cookies[0].value);
    if (uploads.length === 1) await firstPut;
    return new Response(null, { status: 200 });
  } });

  const periodic = supervisor.checkpoint();
  await new Promise(resolve => setImmediate(resolve));
  const final = supervisor.checkpoint();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(uploads, ['old']);
  releaseFirst();
  await Promise.all([periodic, final]);
  assert.deepEqual(uploads, ['old', 'new']);
});
test('entrypoint provisions the largest accepted viewport as Xvfb maximum', async () => {
  const entrypoint = await readFile(new URL('../entrypoint.sh', import.meta.url), 'utf8');
  assert.match(entrypoint, /AGENTHUB_BROWSER_SCREEN:-2560x1600/);
});

test('entrypoint keeps Chromium alive for the full final-checkpoint budget', async () => {
  const entrypoint = await readFile(new URL('../entrypoint.sh', import.meta.url), 'utf8');
  assert.match(entrypoint, /attempts" -lt 47/);
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


test('clipboard paste inserts text into the focused page, not a background tab', async () => {
  const { context, events } = clipboardContext([
    { id: 'background', frames: [{ focused: false, visible: false, text: '' }] },
    { id: 'visible', frames: [{ focused: true, visible: true, text: '' }, { focused: false, visible: true, text: '' }] },
  ]);
  const supervisor = makeSupervisor({ context });

  await supervisor.paste('hello\nworld');

  assert.deepEqual(events, [
    ['session', 'visible'], ['visible', 'Input.insertText', { text: 'hello\nworld' }], ['detach', 'visible'],
  ]);
});

test('clipboard paste falls back to the visible tab when no frame reports focus', async () => {
  const { context, events } = clipboardContext([
    { id: 'hidden', frames: [{ focused: false, visible: false, text: '' }] },
    { id: 'shown', frames: [{ focused: false, visible: true, text: '' }] },
  ]);
  await makeSupervisor({ context }).paste('x');
  assert.deepEqual(events[0], ['session', 'shown']);
});

test('clipboard copy reads the focused frame before Chromium runs its copy or cut command', async () => {
  const { context, events } = clipboardContext([
    { id: 'page', frames: [{ focused: false, visible: true, text: 'outer' }, { focused: true, visible: true, text: 'inner' }] },
  ]);
  const supervisor = makeSupervisor({ context });

  assert.equal(await supervisor.copy(false), 'inner');
  assert.equal(await supervisor.copy(true), 'inner');

  const commands = events.filter(event => event[1] === 'Input.dispatchKeyEvent').map(event => [event[2].type, event[2].commands, event[2].key, event[2].modifiers]);
  assert.deepEqual(commands, [
    ['rawKeyDown', ['copy'], 'c', 2], ['keyUp', undefined, 'c', 2],
    ['rawKeyDown', ['cut'], 'x', 2], ['keyUp', undefined, 'x', 2],
  ]);
});

test('clipboard copy with nothing selected leaves the page untouched', async () => {
  const { context, events } = clipboardContext([{ id: 'page', frames: [{ focused: true, visible: true, text: '' }] }]);
  assert.equal(await makeSupervisor({ context }).copy(true), '');
  assert.deepEqual(events, []);
});

test('clipboard HTTP handler validates input and keeps clipboard text out of logs', async () => {
  const supervisor = makeSupervisor();
  const pasted = [];
  supervisor.paste = async text => { pasted.push(text); };
  supervisor.copy = async cut => (cut ? 'cut text' : 'copied text');

  assert.equal((await invokeClipboard(supervisor, '/clipboard/paste', JSON.stringify({ text: 'p' }))).status, 204);
  assert.deepEqual(pasted, ['p']);
  assert.equal((await invokeClipboard(supervisor, '/clipboard/paste', JSON.stringify({}))).status, 400);
  assert.equal((await invokeClipboard(supervisor, '/clipboard/paste', '{broken')).status, 400);
  assert.equal((await invokeClipboard(supervisor, '/clipboard/paste',
    JSON.stringify({ text: 'x'.repeat(supervisorModule.MAX_CLIPBOARD_CHARS + 1) }))).status, 413);

  const copied = await invokeClipboard(supervisor, '/clipboard/copy', JSON.stringify({ cut: true }));
  assert.equal(copied.status, 200);
  assert.equal(copied.headers['Cache-Control'], 'no-store');
  assert.deepEqual(JSON.parse(copied.body), { text: 'cut text' });

  const warnings = [];
  const warn = console.warn;
  console.warn = message => warnings.push(message);
  try {
    supervisor.paste = async () => { throw new Error('CDP closed'); };
    assert.equal((await invokeClipboard(supervisor, '/clipboard/paste', JSON.stringify({ text: 'very private' }))).status, 500);
  } finally {
    console.warn = warn;
  }
  assert.equal(warnings.length, 1);
  assert.ok(!warnings[0].includes('very private'));
});

test('frame selection reads input ranges, document selections and never a password', () => {
  const previous = globalThis.document;
  const doc = active => ({
    activeElement: active, hasFocus: () => true, visibilityState: 'visible',
    getSelection: () => ({ toString: () => 'page text' }),
  });
  try {
    globalThis.document = doc({ tagName: 'INPUT', type: 'text', value: 'alpha beta', selectionStart: 6, selectionEnd: 10 });
    assert.deepEqual(supervisorModule.readFrameSelection(), { focused: true, visible: true, text: 'beta' });
    globalThis.document = doc({ tagName: 'INPUT', type: 'password', value: 'secret', selectionStart: 0, selectionEnd: 6 });
    assert.equal(supervisorModule.readFrameSelection().text, '');
    globalThis.document = doc({ tagName: 'BODY' });
    assert.equal(supervisorModule.readFrameSelection().text, 'page text');
    globalThis.document = doc({ tagName: 'IFRAME' });
    assert.equal(supervisorModule.readFrameSelection().focused, false);
  } finally {
    globalThis.document = previous;
  }
});

async function invokeViewport(supervisor, body) {
  const request = Readable.from([Buffer.from(body)]);
  request.method = 'PUT';
  request.url = '/viewport';
  const response = {
    status: 0,
    writeHead(status) { this.status = status; },
    end() {},
  };
  await supervisor.handleRequest(request, response);
  return response;
}

function makeSupervisor(overrides = {}) {
  return new BrowserSupervisor({ callbackUrl: 'http://backend.test/internal/browser/lease-1/state-urls', token: 'lease-secret', maxBytes: 1_048_576, checkpointSeconds: 60, fetch: globalThis.fetch, ...overrides });
}
function fakeContext(cookies = []) {
  return { addCookiesCalls: [], async addCookies(value) { this.addCookiesCalls.push(value); }, async cookies() { return cookies; } };
}
function jsonResponse(value) {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

async function invokeClipboard(supervisor, url, body) {
  const request = Readable.from([Buffer.from(body)]);
  request.method = 'POST';
  request.url = url;
  const response = {
    status: 0, headers: {}, body: '',
    writeHead(status, headers = {}) { this.status = status; this.headers = headers; },
    end(chunk = '') { this.body = String(chunk); },
  };
  await supervisor.handleRequest(request, response);
  return response;
}
function clipboardContext(pages) {
  const events = [];
  const fakePages = pages.map(page => ({
    id: page.id,
    frames: () => page.frames.map(state => ({ evaluate: async () => state })),
  }));
  return {
    events,
    context: {
      pages: () => fakePages,
      async newCDPSession(page) {
        events.push(['session', page.id]);
        return {
          async send(method, params) { events.push([page.id, method, params]); return {}; },
          async detach() { events.push(['detach', page.id]); },
        };
      },
    },
  };
}
