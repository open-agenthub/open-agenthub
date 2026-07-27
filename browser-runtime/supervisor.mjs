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
const ALLOWED_SAME_SITE = new Set(['Strict', 'Lax', 'None']);
class ViewportRequestError extends Error {}

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
    await this.execFile('xrandr', ['--display', ':99', '--fb', `${width}x${height}`]);
    const page = this.context?.pages?.()[0];
    if (!page) throw new Error('Chromium did not expose a page for viewport resize');
    const session = await this.context.newCDPSession(page);
    try {
      const { windowId } = await session.send('Browser.getWindowForTarget');
      await session.send('Browser.setWindowBounds', {
        windowId,
        bounds: { left: 0, top: 0, width, height, windowState: 'normal' },
      });
    } finally {
      await session.detach();
    }
  }

  async handleRequest(request, response) {
    if (request.method === 'GET' && request.url === '/healthz') {
      response.writeHead(200, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
      response.end('ok');
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

async function readJsonRequestBounded(request, maxBytes) {
  const chunks = [];
  let total = 0;
  for await (const chunk of request) {
    total += chunk.byteLength;
    if (total > maxBytes) throw new ViewportRequestError('Viewport request exceeds size limit');
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
