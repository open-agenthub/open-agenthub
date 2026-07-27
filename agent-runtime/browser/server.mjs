import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { chromium } from 'playwright-core';
import { z } from 'zod';
import { BrowserBackendClient } from './client.mjs';
import { SnapshotRefs, accessibilitySnapshot } from './snapshot.mjs';

const text = value => ({
  content: [{ type: 'text', text: JSON.stringify(value) }]
});
const image = (data, mimeType) => ({ content: [{ type: 'image', data, mimeType }] });

class BrowserSession {
  constructor(backend = new BrowserBackendClient()) {
    this.backend = backend;
    this.browser = null;
    this.context = null;
    this.page = null;
    this.refs = new SnapshotRefs();
  }

  async start() {
    const connection = await this.backend.start();
    if (!this.browser?.isConnected()) {
      this.browser = await chromium.connectOverCDP(connection.cdpEndpoint, { timeout: 30_000 });
      this.context = this.browser.contexts()[0] ?? await this.browser.newContext();
      this.page = this.context.pages().at(-1) ?? await this.context.newPage();
      this.#watch(this.page);
      this.refs.advanceRevision();
    }
    return connection;
  }

  status() { return this.backend.status(); }

  async stop() {
    this.browser = null;
    this.context = null;
    this.page = null;
    this.refs.advanceRevision();
    return this.backend.stop();
  }

  async navigate(url) {
    const parsed = new URL(url);
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('invalid_browser_url');
    const page = await this.#activePage();
    await page.goto(parsed.toString(), { waitUntil: 'domcontentloaded', timeout: 30_000 });
    return { url: page.url(), title: await page.title() };
  }

  async snapshot() {
    return accessibilitySnapshot(await this.#activePage(), this.refs);
  }

  async click(ref) {
    const locator = this.refs.resolve(ref);
    await locator.click({ timeout: 10_000 });
    return { clicked: ref, url: (await this.#activePage()).url() };
  }

  async type(ref, value, submit = false) {
    const locator = this.refs.resolve(ref);
    await locator.fill(value, { timeout: 10_000 });
    if (submit) await locator.press('Enter');
    return { typed: ref, submitted: submit };
  }

  async screenshot(format = 'png', fullPage = false) {
    const data = await (await this.#activePage()).screenshot({
      type: format, fullPage, timeout: 20_000
    });
    return { data: data.toString('base64'), mimeType: `image/${format === 'jpeg' ? 'jpeg' : 'png'}` };
  }

  async tabs(action, index, url) {
    if (!this.browser?.isConnected()) await this.start();
    if (action === 'select') {
      const pages = this.context.pages();
      if (!Number.isInteger(index) || !pages[index]) throw new Error('invalid_tab_index');
      this.page = pages[index];
      await this.page.bringToFront();
      this.refs.advanceRevision();
    } else if (action === 'new') {
      this.page = await this.context.newPage();
      this.#watch(this.page);
      this.refs.advanceRevision();
      if (url) await this.navigate(url);
    } else if (action === 'close') {
      const pages = this.context.pages();
      if (!Number.isInteger(index) || !pages[index]) throw new Error('invalid_tab_index');
      await pages[index].close();
      this.page = this.context.pages().at(-1) ?? await this.context.newPage();
      this.#watch(this.page);
      this.refs.advanceRevision();
    }
    const pages = this.context.pages();
    return Promise.all(pages.map(async (page, tabIndex) => ({
      index: tabIndex, active: page === this.page, title: await page.title(), url: page.url()
    })));
  }

  async #activePage() {
    if (!this.browser?.isConnected()) await this.start();
    if (this.page?.isClosed()) this.page = this.context.pages().at(-1) ?? await this.context.newPage();
    return this.page;
  }

  #watch(page) {
    if (page.__agenthubWatched) return;
    Object.defineProperty(page, '__agenthubWatched', { value: true });
    page.on('framenavigated', frame => {
      if (frame === page.mainFrame()) this.refs.advanceRevision();
    });
  }
}

const browser = new BrowserSession();
const server = new McpServer({ name: 'agenthub_browser', version: '1.0.0' });
const register = (name, config, handler) => server.registerTool(name, config, async input => {
  try { return await handler(input ?? {}); }
  catch (error) {
    const code = safeError(error);
    return { content: [{ type: 'text', text: JSON.stringify({ error: code }) }], isError: true };
  }
});

register('browser_start', {
  description: 'Start this agent session’s isolated visible Chromium browser.', inputSchema: z.object({})
}, async () => text(await browser.start()));
register('browser_status', {
  description: 'Get the state of this session’s visible browser.', inputSchema: z.object({})
}, async () => text(await browser.status()));
register('browser_stop', {
  description: 'Stop this session’s visible browser and release its resources.', inputSchema: z.object({})
}, async () => text(await browser.stop()));
register('browser_navigate', {
  description: 'Navigate the active browser tab to an HTTP or HTTPS URL.',
  inputSchema: z.object({ url: z.string().url().max(4096) })
}, async ({ url }) => text(await browser.navigate(url)));
register('browser_snapshot', {
  description: 'Return visible page text and current-revision element references.', inputSchema: z.object({})
}, async () => text(await browser.snapshot()));
register('browser_click', {
  description: 'Click an element reference from the latest browser snapshot.',
  inputSchema: z.object({ ref: z.string().regex(/^e\d+-\d+$/).max(64) })
}, async ({ ref }) => text(await browser.click(ref)));
register('browser_type', {
  description: 'Replace text in an element from the latest snapshot.',
  inputSchema: z.object({
    ref: z.string().regex(/^e\d+-\d+$/).max(64),
    text: z.string().max(32_768),
    submit: z.boolean().optional().default(false)
  })
}, async ({ ref, text: value, submit }) => text(await browser.type(ref, value, submit)));
register('browser_screenshot', {
  description: 'Capture the active tab as a PNG or JPEG image.',
  inputSchema: z.object({
    format: z.enum(['png', 'jpeg']).optional().default('png'),
    fullPage: z.boolean().optional().default(false)
  })
}, async ({ format, fullPage }) => {
  const result = await browser.screenshot(format, fullPage);
  return image(result.data, result.mimeType);
});
register('browser_tabs', {
  description: 'List, select, create, or close browser tabs.',
  inputSchema: z.object({
    action: z.enum(['list', 'select', 'new', 'close']).default('list'),
    index: z.number().int().min(0).max(50).optional(),
    url: z.string().url().max(4096).optional()
  })
}, async ({ action, index, url }) => text(await browser.tabs(action, index, url)));

function safeError(error) {
  const message = error instanceof Error ? error.message : '';
  const stable = [
    'browser_backend_not_configured', 'browser_backend_invalid_url',
    'browser_backend_response_too_large', 'browser_backend_invalid_json',
    'stale_browser_reference', 'invalid_browser_url', 'invalid_tab_index'
  ];
  return stable.find(code => message.includes(code)) ??
    (/browser_backend_http_\d{3}/.exec(message)?.[0]) ?? 'browser_operation_failed';
}

await server.connect(new StdioServerTransport());