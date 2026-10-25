import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
const root = process.cwd();
const runtime = process.env.RUNTIME || '/opt/session-agent/';
const require = createRequire(path.join(runtime,'package.json'));
const { Client } = require('@modelcontextprotocol/sdk/client/index.js');
const { StdioClientTransport } = require('@modelcontextprotocol/sdk/client/stdio.js');
const clients = [];
async function connect(id, token) {
  const transport = new StdioClientTransport({ command: process.execPath, args: [root+'/agent-runtime/files/server.mjs'], env: {
    ...process.env, RUNTIME:runtime, AGENTHUB_CALLBACK_URL:`http://127.0.0.1:8080/internal/sessions/${id}`, AGENTHUB_CALLBACK_TOKEN:token,
    AGENTHUB_WORKDIR:root, AGENTHUB_FILE_ROOT:root+`/tests/FileTypesProof/cache.local/${id}`
  }});
  const client = new Client({name:'file-proof',version:'1'}); await client.connect(transport); clients.push(client); return client;
}
async function call(client,name,args) {
  const result = await client.callTool({name,arguments:args}); assert.ok(!result.isError,JSON.stringify(result)); return result;
}
try {
  const producer = await connect('s1','proof-token'), consumer = await connect('s2','sibling-token');
  const directory = root+'/tests/FileTypesProof/input.local'; await fs.mkdir(directory,{recursive:true});
  for(const [name,bytes] of [['mcp.svg','<svg xmlns="http://www.w3.org/2000/svg"/>'],['mcp.zip',Buffer.from([0,255,1])],['LICENSE','extensionless']]) {
    const source = directory+'/'+name; await fs.writeFile(source,bytes);
    const uploaded = (await call(producer,'upload_file',{path:source})).structuredContent;
    const own = await call(producer,'read_file',{fileId:uploaded.id});
    assert.equal(own.content.length,1); assert.equal(own.content[0].type,'text');
    const listing = (await call(consumer,'list_project_files',{sessionId:'s1'})).structuredContent;
    assert.ok(listing.files.some(f=>f.id===uploaded.id));
    const fetched = (await call(consumer,'fetch_project_file',{sessionId:'s1',fileId:uploaded.id})).structuredContent;
    assert.deepEqual(await fs.readFile(fetched.localPath),Buffer.from(bytes));
    console.log(`PASS real stdio MCP ${name}: upload/read, sibling list/fetch byte-identical, never inline`);
  }
} finally { for(const client of clients) await client.close(); }
