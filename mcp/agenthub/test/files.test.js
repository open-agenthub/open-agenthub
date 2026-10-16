import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { AgentHubClient } from '../client.mjs';
import { readSessionFile } from '../files.mjs';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { fileURLToPath } from 'node:url';

const fileId = '0123456789abcdef0123456789abcdef';
const file = { id: fileId, name: '../../shot.png', mimeType: 'image/png', size: 3 };

test('stdio MCP exposes file tools and downloads through an authenticated tool call', async () => {
  const root = await mkdtemp(join(tmpdir(), 'agenthub-mcp-test-'));
  const server = http.createServer((req, res) => {
    if (req.headers.authorization !== 'Bearer oah_test') { res.writeHead(401); res.end(); return; }
    if (req.url.endsWith('/content')) res.end(Buffer.from([1, 2, 3]));
    else { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify([file])); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const client = new Client({ name: 'files-integration-test', version: '1.0.0' });
  try {
    await client.connect(new StdioClientTransport({
      command: process.execPath,
      args: [fileURLToPath(new URL('../server.mjs', import.meta.url))],
      env: { ...process.env, HOME: root, USERPROFILE: root,
        AGENTHUB_URL: `http://127.0.0.1:${server.address().port}`, AGENTHUB_TOKEN: 'oah_test' }
    }));
    const tools = await client.listTools();
    assert.ok(tools.tools.some(tool => tool.name === 'list_files'));
    assert.ok(tools.tools.some(tool => tool.name === 'read_file'));
    const listed = await client.callTool({ name: 'list_files', arguments: { sessionId: 's1' } });
    assert.equal(JSON.parse(listed.content[0].text).files[0].id, fileId);
    const read = await client.callTool({ name: 'read_file', arguments: { sessionId: 's1', fileId } });
    assert.ok(!read.isError);
    assert.equal(read.content[0].type, 'image');
    assert.ok(read.structuredContent.localPath.startsWith(root));
    assert.deepEqual(await readFile(read.structuredContent.localPath), Buffer.from([1, 2, 3]));
  } finally {
    await client.close();
    await new Promise(resolve => server.close(resolve));
    await rm(root, { recursive: true, force: true });
  }
});

test('authenticated HTTP download writes local bytes and returns image content without storage URLs', async () => {
  const root = await mkdtemp(join(tmpdir(), 'agenthub-file-test-'));
  const calls = [];
  const server = http.createServer((req, res) => {
    calls.push(req.url);
    assert.equal(req.headers.authorization, 'Bearer oah_test');
    if (req.url.endsWith('/content')) res.end(Buffer.from([1, 2, 3]));
    else { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify([file])); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const client = new AgentHubClient({ AGENTHUB_URL: `http://127.0.0.1:${server.address().port}`, AGENTHUB_TOKEN: 'oah_test' });
    const result = await readSessionFile(client, { sessionId: 's1', fileId }, root);
    assert.deepEqual(await readFile(result.structuredContent.localPath), Buffer.from([1, 2, 3]));
    assert.ok(result.structuredContent.localPath.startsWith(root));
    assert.equal(result.content[0].type, 'image');
    assert.equal(result.content[0].data, 'AQID');
    assert.deepEqual(calls, ['/api/remote/sessions/s1/files', `/api/remote/sessions/s1/files/${fileId}/content`]);
  } finally {
    await new Promise(resolve => server.close(resolve));
    await rm(root, { recursive: true, force: true });
  }
});

test('missing files and size mismatches do not create a local file', async () => {
  const client = { listFiles: async () => [], fileContent: async () => { throw new Error('must not download'); } };
  await assert.rejects(readSessionFile(client, { sessionId: 's1', fileId }), /file_not_found/);
  client.listFiles = async () => [file];
  client.fileContent = async () => new Uint8Array([1]);
  await assert.rejects(readSessionFile(client, { sessionId: 's1', fileId }), /file_size_mismatch/);
  await assert.rejects(readSessionFile(client, { sessionId: '..', fileId }), /agenthub_invalid_json/);
});

test('download rejects oversized declared and streamed content and HTTP errors', async () => {
  for (const response of [
    new Response('123', { headers: { 'Content-Length': '3' } }),
    new Response('123'),
  ]) {
    const client = new AgentHubClient({ AGENTHUB_URL: 'https://hub.example.com', AGENTHUB_TOKEN: 'oah_test' }, async () => response);
    await assert.rejects(client.fileContent('s1', fileId, 2), /agenthub_response_too_large/);
  }
  const client = new AgentHubClient({ AGENTHUB_URL: 'https://hub.example.com', AGENTHUB_TOKEN: 'oah_test' }, async (_, init) => {
    assert.equal(init.redirect, 'error');
    return new Response('secret', { status: 403 });
  });
  await assert.rejects(client.fileContent('s1', fileId), /^Error: agenthub_http_403$/);
});
