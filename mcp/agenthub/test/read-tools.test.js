import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

test('stdio read tools expose backend pages without the session allowlist', async () => {
  const requests = [];
  const replies = {
    '/api/remote/projects': [{ id: 'p', name: 'Project', color: null, sortOrder: 0 }],
    '/api/remote/projects/p': { id: 'p', name: 'Project', color: null, sortOrder: 0 },
    '/api/remote/mcp-servers': [{ id: 'm', name: 'Catalog' }],
    '/api/remote/sessions/s/messages?cursor=opaque%2B%2F%3D&limit=2': {
      messages: [{ id: 'msg', body: 'hello', deliveredAt: null }], nextCursor: 'next'
    },
    '/api/remote/sessions/s/conversation?offset=1&max=2': {
      source: 'native', entries: [{ role: 'assistant', text: 'hello', at: null }], nextOffset: 2
    }
  };
  const server = http.createServer((req, res) => {
    requests.push(req.url);
    assert.equal(req.headers.authorization, 'Bearer oah_protocol');
    assert.equal(req.method, 'GET');
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify(replies[req.url]));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL('../server.mjs', import.meta.url))],
    env: { ...process.env, AGENTHUB_URL: `http://127.0.0.1:${server.address().port}`, AGENTHUB_TOKEN: 'oah_protocol' },
    stderr: 'pipe'
  });
  const client = new Client({ name: 'read-proof', version: '1.0.0' });
  try {
    await client.connect(transport);
    const tools = new Set((await client.listTools()).tools.map(tool => tool.name));
    const cases = [
      ['projects_list', {}, '/api/remote/projects'],
      ['project_get', { id: 'p' }, '/api/remote/projects/p'],
      ['mcp_servers_list', {}, '/api/remote/mcp-servers'],
      ['session_messages', { id: 's', cursor: 'opaque+/=', limit: 2 }, '/api/remote/sessions/s/messages?cursor=opaque%2B%2F%3D&limit=2'],
      ['session_conversation', { id: 's', offset: 1, max: 2 }, '/api/remote/sessions/s/conversation?offset=1&max=2']
    ];
    for (const [name, args, path] of cases) {
      assert.ok(tools.has(name));
      const result = await client.callTool({ name, arguments: args });
      assert.ok(!result.isError, JSON.stringify(result));
      assert.deepEqual(JSON.parse(result.content[0].text), replies[path]);
    }
    assert.deepEqual(requests, cases.map(row => row[2]));
  } finally {
    await client.close();
    await new Promise(resolve => server.close(resolve));
  }
});
