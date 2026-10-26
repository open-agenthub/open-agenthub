import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { chromium } from 'playwright-core';
import { z } from 'zod';
import { BrowserBackendClient } from './client.mjs';
import { BrowserSession } from './session.mjs';

const text = value => ({
  content: [{ type: 'text', text: JSON.stringify(value) }]
});
const image = (data, mimeType) => ({ content: [{ type: 'image', data, mimeType }] });

const browser = new BrowserSession(new BrowserBackendClient(), chromium);
const server = new McpServer({ name: 'agenthub_browser', version: '1.0.0' });
// Rejecters of the calls in flight, so an error nobody awaited can fail them instead of leaving
// them waiting on a connection that is being dropped.
const inFlight = new Set();
const register = (name, config, handler) => server.registerTool(name, config, async input => {
  let fault;
  const faulted = new Promise((_, reject) => { fault = reject; });
  inFlight.add(fault);
  try { return await Promise.race([handler(input ?? {}), faulted]); }
  catch (error) {
    const code = await browser.recover() ? 'browser_connection_lost' : safeError(error);
    return { content: [{ type: 'text', text: JSON.stringify({ error: code }) }], isError: true };
  } finally {
    inFlight.delete(fault);
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
    'stale_browser_reference', 'invalid_browser_url', 'invalid_tab_index',
    'browser_context_unavailable'
  ];
  if (error?.name === 'TimeoutError') return 'browser_action_timeout';
  return stable.find(code => message.includes(code)) ??
    (/browser_backend_http_\d{3}/.exec(message)?.[0]) ?? 'browser_operation_failed';
}

// Node ends the process on an error nobody awaited, and the agent CLI then reports every later
// browser tool as "Transport closed" until the session restarts (#237). Such an error comes from
// the browser connection, never from the MCP transport: the calls in flight fail, the connection
// is dropped and the next call connects again. stderr only: stdout is the MCP stream.
for (const event of ['uncaughtException', 'unhandledRejection']) {
  process.on(event, error => {
    console.error(`[agenthub-browser] ${event}: ${error instanceof Error ? error.message : String(error)}`);
    for (const fail of inFlight) fail(new Error('browser_operation_failed'));
    browser.drop();
  });
}

await server.connect(new StdioServerTransport());