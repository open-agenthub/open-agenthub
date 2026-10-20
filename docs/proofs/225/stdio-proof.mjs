// Exercise the shipped servers and installed SDK over real pipes and HTTP. The
// callback stand-in records requests so schema/transport regressions cannot hide in mocks.
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const scratch = await fs.mkdtemp(path.join(os.tmpdir(), 'dependency-stdio-'));
const calls = [];
const fixture = http.createServer((req, res) => {
  calls.push({ path: req.url, authorization: req.headers.authorization, callbackToken: req.headers['x-agent-token'] });
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify([{ id: 'proof-child', title: 'Proof child', phase: 'Running' }]));
});
await new Promise(resolve => fixture.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${fixture.address().port}`;
try {
  await fs.cp(path.join(root, 'agent-runtime/sessions'), path.join(scratch, 'sessions'), { recursive: true });
  await fs.symlink(path.join(root, 'agent-runtime/session-agent/node_modules'), path.join(scratch, 'node_modules'), 'dir');
  for (const config of [
    { name: 'personal', modules: 'mcp/agenthub/node_modules', server: path.join(root, 'mcp/agenthub/server.mjs'), env: { AGENTHUB_URL: base, AGENTHUB_TOKEN: 'proof-token' }, expected: '/api/remote/sessions', auth: c => c.authorization === 'Bearer proof-token' },
    { name: 'in-pod', modules: 'agent-runtime/session-agent/node_modules', server: path.join(scratch, 'sessions/server.mjs'), env: { AGENTHUB_CALLBACK_URL: base + '/internal/sessions/proof', AGENTHUB_CALLBACK_TOKEN: 'proof-token', AGENTHUB_SESSION_ID: 'proof' }, expected: '/internal/sessions/proof/children', auth: c => c.callbackToken === 'proof-token' }
  ]) {
    const sdk = path.join(root, config.modules, '@modelcontextprotocol/sdk/dist/esm');
    const { Client } = await import(pathToFileURL(path.join(sdk, 'client/index.js')));
    const { StdioClientTransport } = await import(pathToFileURL(path.join(sdk, 'client/stdio.js')));
    const client = new Client({ name: 'dependency-proof', version: '1.0.0' });
    const transport = new StdioClientTransport({ command: process.execPath, args: [config.server], env: { PATH: process.env.PATH, ...config.env }, stderr: 'pipe' });
    let stderr = '';
    transport.stderr?.on('data', b => { stderr += b; });
    try {
      await client.connect(transport);
      const tools = await client.listTools();
      assert(tools.tools.some(t => t.name === 'session_list'));
      const result = await client.callTool({ name: 'session_list', arguments: {} });
      assert(!result.isError, JSON.stringify(result));
      assert.equal(JSON.parse(result.content[0].text)[0].id, 'proof-child');
      assert(calls.some(c => c.path === config.expected && config.auth(c)));
      assert.equal(stderr, '');
      console.log(JSON.stringify({ server: config.name, tools: tools.tools.length, sessionList: 'passed', stderr }));
    } finally { await client.close(); }
  }
} finally {
  await new Promise(resolve => fixture.close(resolve));
  await fs.rm(scratch, { recursive: true, force: true });
}
