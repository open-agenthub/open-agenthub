import { execFile as execFileCallback } from 'node:child_process';
import http from 'node:http';
import { promisify } from 'node:util';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright-core';

const execFile = promisify(execFileCallback);

export const COOKIE_STATE_VERSION = 1;
export const MIN_VIEWPORT = { width: 480, height: 320 };
export const MAX_VIEWPORT = { width: 2560, height: 1600 };
const DEFAULT_MAX_BYTES = 1_048_576;
const REQUEST_TIMEOUT_MS = 10_000;
const SHUTDOWN_TIMEOUT_MS = 45_000;
const VIEWPORT_WINDOW_RETRY_ATTEMPTS = 40;
const VIEWPORT_WINDOW_RETRY_DELAY_MS = 100;
const ALLOWED_SAME_SITE = new Set(['Strict', 'Lax', 'None']);
// Bounds one paste or copy so a single request cannot push an unbounded string through the
// supervisor and Chromium's input pipeline; far above anything typed into a form.
export const MAX_CLIPBOARD_CHARS = 262_144;
const MAX_CLIPBOARD_REQUEST_BYTES = 2 * 1024 * 1024;
class ViewportRequestError extends Error {}
class ClipboardRequestError extends Error {
  constructor(message, status = 400) { super(message); this.status = status; }
}

export function validateClipboardText(value) {
  const text = value?.text;
  if (typeof text !== 'string') throw new ClipboardRequestError('Clipboard text is missing');
  if (text.length > MAX_CLIPBOARD_CHARS) throw new ClipboardRequestError('Clipboard text exceeds size limit', 413);
  return text;
}

// Runs inside every frame of a page. The frame whose document has focus and whose focused
// element is not itself a frame holds the caret; asking only the top document would miss a
// selection inside an iframe, cross-origin ones included, because each frame is evaluated in
// its own context. A password field answers empty, as it does for a native copy.
export function readFrameSelection() {
  const active = document.activeElement;
  const focused = document.hasFocus() && !(active && /^(IFRAME|FRAME)$/.test(active.tagName));
  let text;
  if (active && (active.tagName === 'TEXTAREA' || active.tagName === 'INPUT') &&
      typeof active.selectionStart === 'number') {
    text = active.type === 'password' ? ''
      : active.value.slice(active.selectionStart, active.selectionEnd ?? active.selectionStart);
  } else {
    text = document.getSelection()?.toString() ?? '';
  }
  return { focused, visible: document.visibilityState === 'visible', text };
}

export function decodeCookieState(input, maxBytes = DEFAULT_MAX_BYTES) {
  try {
    const bytes = Buffer.isBuffer(input) ? input : Buffer.from(input);
    if (bytes.byteLength > maxBytes) return [];
    const state = JSON.parse(bytes.toString('utf8'));
    if (!state || state.version !== COOKIE_STATE_VERSION || !Array.isArray(state.cookies)) return [];
    return state.cookies.slice(0, 10_000).map(normalizeCookie).filter(Boolean);
  } catch { return []; }
}

export function encodeCookieState(cookies, maxBytes = DEFAULT_MAX_BYTES) {
  const safe = Array.isArray(cookies) ? cookies.slice(0, 10_000).map(normalizeCookie).filter(Boolean) : [];
  const body = JSON.stringify({ version: COOKIE_STATE_VERSION, cookies: safe });
  if (Buffer.byteLength(body) > maxBytes) throw new Error('Cookie state exceeds configured size limit');
  return body;
}

export function validateViewport(value) {
  const width = value?.width;
  const height = value?.height;
  if (!Number.isInteger(width) || !Number.isInteger(height) ||
      width < MIN_VIEWPORT.width || height < MIN_VIEWPORT.height ||
      width > MAX_VIEWPORT.width || height > MAX_VIEWPORT.height) {
    throw new ViewportRequestError('Viewport dimensions are invalid');
  }
  return { width, height };
}

function normalizeCookie(cookie) {
  if (!cookie || typeof cookie !== 'object') return null;
  if (!nonEmpty(cookie.name) || typeof cookie.value !== 'string' || !nonEmpty(cookie.domain) || !nonEmpty(cookie.path)) return null;
  if (!Number.isFinite(cookie.expires) || typeof cookie.httpOnly !== 'boolean' || typeof cookie.secure !== 'boolean') return null;
  if (!ALLOWED_SAME_SITE.has(cookie.sameSite)) return null;
  return { name: cookie.name, value: cookie.value, domain: cookie.domain, path: cookie.path,
    expires: cookie.expires, httpOnly: cookie.httpOnly, secure: cookie.secure, sameSite: cookie.sameSite };
}
function nonEmpty(value) { return typeof value === 'string' && value.length > 0 && value.length <= 4096; }

export class BrowserSupervisor {
  constructor(options = {}) {
    this.callbackUrl = options.callbackUrl ?? process.env.AGENTHUB_BROWSER_CALLBACK_URL ?? '';
    this.token = options.token ?? process.env.AGENTHUB_BROWSER_LEASE_TOKEN ?? '';
    this.maxBytes = positiveInt(options.maxBytes ?? process.env.AGENTHUB_BROWSER_COOKIE_MAX_BYTES, DEFAULT_MAX_BYTES);
    this.checkpointSeconds = positiveInt(options.checkpointSeconds ?? process.env.AGENTHUB_BROWSER_COOKIE_CHECKPOINT_SECONDS, 60);
    this.cdpUrl = options.cdpUrl ?? 'http://127.0.0.1:9223';
    this.fetch = options.fetch ?? globalThis.fetch;
    this.connect = options.connect ?? (url => chromium.connectOverCDP(url));
    this.setInterval = options.setInterval ?? globalThis.setInterval;
    this.clearInterval = options.clearInterval ?? globalThis.clearInterval;
    this.execFile = options.execFile ?? execFile;
    this.sleep = options.sleep ?? (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)));
    this.resizeTail = Promise.resolve();
    this.context = options.context;
    this.browser = options.browser;
    this.timer = null;
    this.healthServer = null;
    this.shuttingDown = false;
    this.checkpointTail = Promise.resolve();
  }

  resizeViewport(width, height) {
    const viewport = validateViewport({ width, height });
    const operation = this.resizeTail.then(() => this.resizeViewportOnce(viewport));
    this.resizeTail = operation.catch(() => undefined);
    return operation;
  }

  async resizeViewportOnce({ width, height }) {
    try {
      await this.execFile('xrandr', ['--display', ':99', '--fb', `${width}x${height}`]);
    } catch (error) {
      let stdout = '';
      try {
        ({ stdout = '' } = await this.execFile('xrandr', ['--display', ':99', '--current']));
      } catch {
        throw error;
      }
      const current = /\bcurrent\s+(\d+)\s+x\s+(\d+)\b/.exec(stdout);
      if (!current || Number(current[1]) !== width || Number(current[2]) !== height)
        throw error;
    }
    const page = this.context?.pages?.()[0];
    if (!page) throw new Error('Chromium did not expose a page for viewport resize');
    for (let attempt = 0; ; attempt += 1) {
      let session;
      try {
        session = await this.context.newCDPSession(page);
        const { windowId } = await session.send('Browser.getWindowForTarget');
        await session.send('Browser.setWindowBounds', {
          windowId,
          bounds: { left: 0, top: 0, width, height, windowState: 'normal' },
        });
        return;
      } catch (error) {
        if (attempt >= VIEWPORT_WINDOW_RETRY_ATTEMPTS - 1) throw error;
      } finally {
        await session?.detach().catch(() => undefined);
      }
      await this.sleep(VIEWPORT_WINDOW_RETRY_DELAY_MS);
    }
  }

  // The page the user is looking at: the one holding focus, else the visible tab. Background
  // tabs report 'hidden', so a paste never lands in a tab nobody sees.
  async focusedTarget() {
    const pages = this.context?.pages?.() ?? [];
    let fallback = null;
    for (const page of pages) {
      const frames = page.frames?.() ?? [page.mainFrame()];
      const states = await Promise.all(frames.map(frame =>
        frame.evaluate(readFrameSelection).catch(() => null)));
      const focused = states.findLastIndex(state => state?.focused);
      if (focused >= 0) return { page, state: states[focused] };
      if (!fallback && states[0]?.visible) fallback = { page, state: states[0] };
    }
    if (fallback) return fallback;
    if (pages[0]) return { page: pages[0], state: { text: '' } };
    throw new Error('Chromium did not expose a page for the clipboard');
  }

  async paste(text) {
    if (!text) return;
    const { page } = await this.focusedTarget();
    const session = await this.context.newCDPSession(page);
    try {
      await session.send('Input.insertText', { text });
    } finally {
      await session.detach().catch(() => undefined);
    }
  }

  // Reads the selection first, then lets Chromium run its own copy or cut command, so the page
  // still sees the shortcut it expects and a cut removes the text it handed over. Reading after
  // the cut would find the selection already gone.
  async copy(cut) {
    const { page, state } = await this.focusedTarget();
    const text = state?.text ?? '';
    if (text.length > MAX_CLIPBOARD_CHARS) throw new ClipboardRequestError('Selection exceeds size limit', 413);
    if (!text) return '';
    const key = cut ? 'x' : 'c';
    const event = {
      key, code: `Key${key.toUpperCase()}`, modifiers: 2,
      windowsVirtualKeyCode: key.toUpperCase().charCodeAt(0),
    };
    const session = await this.context.newCDPSession(page);
    try {
      await session.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', ...event, commands: [cut ? 'cut' : 'copy'] });
      await session.send('Input.dispatchKeyEvent', { type: 'keyUp', ...event });
    } finally {
      await session.detach().catch(() => undefined);
    }
    return text;
  }

  // Clipboard text is the user's own data: a failure is reported by status only and the text
  // never reaches a log line.
  async handleClipboardRequest(request, response) {
    try {
      const body = await readJsonRequestBounded(request, MAX_CLIPBOARD_REQUEST_BYTES, ClipboardRequestError);
      if (request.url === '/clipboard/paste') {
        await this.paste(validateClipboardText(body));
        response.writeHead(204, { 'Cache-Control': 'no-store' });
        response.end();
        return;
      }
      const text = await this.copy(body?.cut === true);
      response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify({ text }));
    } catch (error) {
      const invalid = error instanceof SyntaxError || error instanceof ClipboardRequestError;
      if (!invalid) diagnostic('clipboard request failed', error);
      response.writeHead(error instanceof ClipboardRequestError ? error.status : invalid ? 400 : 500);
      response.end();
    }
  }

  async handleRequest(request, response) {
    if (request.method === 'GET' && request.url === '/healthz') {
      response.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
      response.end('ok');
      return;
    }
    if (request.method === 'POST' && (request.url === '/clipboard/paste' || request.url === '/clipboard/copy')) {
      await this.handleClipboardRequest(request, response);
      return;
    }
    if (request.method !== 'PUT' || request.url !== '/viewport') {
      response.writeHead(404);
      response.end();
      return;
    }
    try {
      const viewport = validateViewport(await readJsonRequestBounded(request, 1024));
      await this.resizeViewport(viewport.width, viewport.height);
      response.writeHead(204);
    } catch (error) {
      if (!(error instanceof SyntaxError) && !(error instanceof ViewportRequestError))
        diagnostic('viewport resize failed', error);
      response.writeHead(error instanceof SyntaxError || error instanceof ViewportRequestError ? 400 : 500);
    }
    response.end();
  }

  async start() {
    await waitForCdp(this.cdpUrl, this.fetch);
    this.browser = await this.connect(this.cdpUrl);
    this.context = this.browser.contexts()[0];
    if (!this.context) throw new Error('Chromium did not expose a default context');
    await this.restore();
    this.timer = this.setInterval(() => void this.checkpoint(), this.checkpointSeconds * 1000);
    this.timer.unref?.();
    this.healthServer = http.createServer((request, response) => {
      void this.handleRequest(request, response);
    });
    await new Promise((resolve, reject) => {
      this.healthServer.once('error', reject);
      this.healthServer.listen(6081, '0.0.0.0', resolve);
    });
  }

  async stateUrls() {
    if (!this.callbackUrl || !this.token) return null;
    try {
      const response = await fetchWithTimeout(this.fetch, this.callbackUrl, { headers: { 'X-Browser-Token': this.token } });
      if (!response.ok) return null;
      const payload = await readJsonBounded(response, 16_384);
      return payload && typeof payload.getUrl === 'string' && typeof payload.putUrl === 'string' ? payload : null;
    } catch (error) { diagnostic('state URL request failed', error); return null; }
  }

  async restore() {
    if (!this.context) return false;
    const urls = await this.stateUrls();
    if (!urls?.getUrl) return false;
    try {
      const response = await fetchWithTimeout(this.fetch, urls.getUrl);
      if (response.status === 404) return false;
      if (!response.ok) throw new Error(`storage returned ${response.status}`);
      const cookies = decodeCookieState(await readBytesBounded(response, this.maxBytes), this.maxBytes);
      if (cookies.length === 0) return false;
      await this.context.addCookies(cookies);
      return true;
    } catch (error) { diagnostic('cookie restore ignored', error); return false; }
  }

  checkpoint() {
    const operation = this.checkpointTail.then(() => this.checkpointOnce());
    this.checkpointTail = operation.catch(() => false);
    return operation;
  }

  async checkpointOnce() {
    if (!this.context) return false;
    try {
      const urls = await this.stateUrls();
      if (!urls?.putUrl) return false;
      const body = encodeCookieState(await this.context.cookies(), this.maxBytes);
      const response = await fetchWithTimeout(this.fetch, urls.putUrl, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body,
      });
      if (!response.ok) throw new Error(`storage returned ${response.status}`);
      return true;
    } catch (error) { diagnostic('cookie checkpoint skipped', error); return false; }
  }

  async shutdown() {
    if (this.shuttingDown) return;
    this.shuttingDown = true;
    if (this.timer) this.clearInterval(this.timer);
    await Promise.race([this.checkpoint(), new Promise(resolve => { const timer = setTimeout(resolve, SHUTDOWN_TIMEOUT_MS); timer.unref?.(); })]);
    if (this.healthServer) await new Promise(resolve => this.healthServer.close(resolve));
    await this.browser?.close().catch(error => diagnostic('browser disconnect failed', error));
  }
}

async function readJsonRequestBounded(request, maxBytes, ErrorType = ViewportRequestError) {
  const chunks = [];
  let total = 0;
  for await (const chunk of request) {
    total += chunk.byteLength;
    if (total > maxBytes) throw new ErrorType('Request exceeds size limit', 413);
    chunks.push(Buffer.from(chunk));
  }
  return JSON.parse(Buffer.concat(chunks, total).toString('utf8'));
}

async function waitForCdp(url, fetchImpl) {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetchWithTimeout(fetchImpl, `${url}/json/version`, {}, 2_000);
      if (response.ok) return;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error('Chromium CDP readiness timed out');
}
async function fetchWithTimeout(fetchImpl, url, options = {}, timeoutMs = REQUEST_TIMEOUT_MS) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try { return await fetchImpl(url, { ...options, signal: controller.signal }); }
  finally { clearTimeout(timer); }
}
async function readBytesBounded(response, maxBytes) {
  const declared = Number(response.headers.get('content-length'));
  if (Number.isFinite(declared) && declared > maxBytes) throw new Error('response exceeds configured size limit');
  const reader = response.body?.getReader();
  if (!reader) return Buffer.alloc(0);
  const chunks = []; let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxBytes) { await reader.cancel(); throw new Error('response exceeds configured size limit'); }
    chunks.push(Buffer.from(value));
  }
  return Buffer.concat(chunks, total);
}
async function readJsonBounded(response, maxBytes) { return JSON.parse((await readBytesBounded(response, maxBytes)).toString('utf8')); }
function positiveInt(value, fallback) { const parsed = Number.parseInt(value, 10); return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : fallback; }
function diagnostic(message, error) { console.warn(`[browser-supervisor] ${message}: ${error instanceof Error ? error.message : 'unknown error'}`); }

async function main() {
  const supervisor = new BrowserSupervisor();
  const terminate = async () => { await supervisor.shutdown(); process.exit(0); };
  process.once('SIGTERM', terminate);
  process.once('SIGINT', terminate);
  await supervisor.start();
  console.log('[browser-supervisor] ready');
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { diagnostic('startup failed', error); process.exit(1); });
}
