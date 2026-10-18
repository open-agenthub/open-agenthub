'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');

test('real in-pod MCP clears only its callback identity and refuses a peer target', async t => {
  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { StdioClientTransport } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  // The image puts sessions and node_modules beside each other; source tests install
  // dependencies under session-agent instead, so reproduce the image layout in isolation.
  const runtime = fs.mkdtempSync(path.join(os.tmpdir(), 'context-clear-mcp-'));
  t.after(() => fs.rmSync(runtime, { recursive: true, force: true }));
  fs.cpSync(path.resolve(__dirname, '../../sessions'), path.join(runtime, 'sessions'), { recursive: true });
  fs.symlinkSync(path.resolve(__dirname, '../node_modules'), path.join(runtime, 'node_modules'), 'dir');
  const requests = [];
  const backend = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      requests.push({ url: req.url, token: req.headers['x-agent-token'], body: JSON.parse(body) });
      res.setHeader('Content-Type', 'application/json');
      res.end('{"scheduled":true}');
    });
  });
  await new Promise(resolve => backend.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => backend.close(resolve)));
  const transport = new StdioClientTransport({ command: process.execPath,
    args: [path.join(runtime, 'sessions/server.mjs')],
    env: { ...process.env, AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${backend.address().port}/internal/sessions/self`,
      AGENTHUB_CALLBACK_TOKEN: 'self-token', AGENTHUB_SESSION_ID: 'self' }, stderr: 'pipe' });
  const client = new Client({ name: 'context-clear-proof', version: '1.0.0' });
  t.after(() => client.close());
  await client.connect(transport);
  const tool = (await client.listTools()).tools.find(tool => tool.name === 'context_clear');
  assert.ok(tool);
  assert.equal(tool.inputSchema.properties.sessionId, undefined);
  const result = await client.callTool({ name: 'context_clear', arguments: { followUp: 'State: tests passed. Next: review the patch.' } });
  assert.deepEqual(JSON.parse(result.content[0].text), { scheduled: true });
  assert.deepEqual(requests, [{ url: '/internal/sessions/self/context-clear', token: 'self-token', body: { followUp: 'State: tests passed. Next: review the patch.' } }]);
  const peer = await client.callTool({ name: 'context_clear', arguments: { sessionId: 'child', followUp: 'Continue' } });
  assert.equal(peer.isError, true);
  const empty = await client.callTool({ name: 'context_clear', arguments: { followUp: '' } });
  assert.equal(empty.isError, true);
  assert.equal(requests.length, 1);
});
