'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { Client } = require('@modelcontextprotocol/sdk/client/index.js');
const { StdioClientTransport } = require('@modelcontextprotocol/sdk/client/stdio.js');

// The real server over stdio, laid out as in the image (/opt/session-agent/{browser,node_modules}).
// A preload throws and rejects asynchronously, standing in for an error inside the browser
// connection that nothing awaits: it must fail the call in flight, not end the process (#237).
// The fault fires once the backend has the call, not after a fixed delay from start: on a slow
// runner the server took longer than that delay to answer initialize, so the error landed before
// any call was in flight and the call succeeded.
test('an error nobody awaited fails the call in flight and leaves the browser MCP answering', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'browser-mcp-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.symlinkSync(path.join(__dirname, '..', 'node_modules'), path.join(root, 'node_modules'));
  fs.cpSync(path.join(__dirname, '..', '..', 'browser'), path.join(root, 'browser'), { recursive: true });
  const preload = path.join(root, 'fault.mjs');
  const trigger = path.join(root, 'in-flight');
  fs.writeFileSync(preload, `
    import fs from 'node:fs';
    const poll = setInterval(() => {
      if (!fs.existsSync(${JSON.stringify(trigger)})) return;
      clearInterval(poll);
      setTimeout(() => { throw new Error('boom'); }, 0);
      setTimeout(() => { Promise.reject(new Error('later')); }, 300);
    }, 20);
  `);

  let delay = 1_500;
  let triggered = false;
  const backend = http.createServer((request, response) => {
    if (!triggered) { triggered = true; fs.writeFileSync(trigger, ''); }
    setTimeout(() => {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify({ phase: 'Running' }));
    }, delay);
  });
  await new Promise(resolve => backend.listen(0, '127.0.0.1', resolve));
  t.after(() => backend.close());

  const transport = new StdioClientTransport({
    command: process.execPath,
    args: ['--import', preload, path.join(root, 'browser', 'server.mjs')],
    env: {
      ...process.env,
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${backend.address().port}/internal/sessions/s1`,
      AGENTHUB_CALLBACK_TOKEN: 'token'
    },
    stderr: 'pipe'
  });
  let stderr = '';
  transport.stderr.on('data', chunk => { stderr += chunk; });
  const client = new Client({ name: 'guard-test', version: '1' });
  let closed = false;
  client.onclose = () => { closed = true; };
  await client.connect(transport);
  t.after(() => client.close());

  const inFlight = await client.callTool({ name: 'browser_status', arguments: {} });
  assert.equal(inFlight.isError, true);
  assert.deepEqual(JSON.parse(inFlight.content[0].text), { error: 'browser_operation_failed' });

  delay = 0;
  // The rejection follows the throw by 300 ms; let both reach the guard before the next call.
  await new Promise(resolve => setTimeout(resolve, 600));
  const next = await client.callTool({ name: 'browser_status', arguments: {} });
  assert.equal(next.isError, undefined);
  assert.deepEqual(JSON.parse(next.content[0].text), { phase: 'Running' });
  assert.equal(closed, false);
  assert.match(stderr, /uncaughtException: boom/);
  assert.match(stderr, /unhandledRejection: later/);
});
