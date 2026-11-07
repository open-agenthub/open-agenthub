import readline from 'node:readline';

// A local stdio MCP target records only fixed synthetic call names, never input bodies.
const lines = readline.createInterface({ input: process.stdin });
for await (const line of lines) {
  let request;
  try { request = JSON.parse(line); } catch { continue; }
  if (request.id === undefined) continue;
  let result = {};
  if (request.method === 'initialize') result = {
    protocolVersion: request.params.protocolVersion,
    capabilities: { tools: {}, resources: {} }, serverInfo: { name: 'proof', version: '1' }
  };
  if (request.method === 'tools/list') result = { tools: ['allowed', 'get-data.v2'].map(name => ({
    name, description: 'Synthetic proof tool', inputSchema: { type: 'object', properties: {} }
  })) };
  if (request.method === 'tools/call') result = { content: [{ type: 'text', text: 'PROOF_EXECUTED_' + request.params.name }] };
  if (request.method === 'resources/list') result = { resources: [{ uri: 'proof://sample', name: 'sample' }] };
  if (request.method === 'resources/templates/list') result = { resourceTemplates: [] };
  if (request.method === 'resources/read') result = { contents: [{ uri: request.params.uri, text: 'PROOF_RESOURCE' }] };
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id: request.id, result }) + '\n');
}
