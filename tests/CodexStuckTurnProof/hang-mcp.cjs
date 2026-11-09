// A stdio MCP server whose "wait" tool never answers, and whose "quick" tool answers at once.
const rl = require('readline').createInterface({ input: process.stdin });
const send = o => process.stdout.write(JSON.stringify(o) + '\n');
rl.on('line', line => {
  let m; try { m = JSON.parse(line); } catch { return; }
  if (m.method === 'initialize') send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'hang', version: '1' } } });
  else if (m.method === 'tools/list') send({ jsonrpc: '2.0', id: m.id, result: { tools: [
    { name: 'wait', description: 'never answers', inputSchema: { type: 'object', properties: {} } },
    { name: 'quick', description: 'answers', inputSchema: { type: 'object', properties: {} } }] } });
  else if (m.method === 'tools/call') { require('fs').appendFileSync(__dirname + '/hang-mcp.log', new Date().toISOString() + ' call ' + m.params.name + '\n'); if (m.params.name === 'quick') send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text', text: 'quick ok' }] } }); }
  else if (m.id !== undefined) send({ jsonrpc: '2.0', id: m.id, result: {} });
});
