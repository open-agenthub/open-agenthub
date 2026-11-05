// Stand-in hub: the session's skill-library MCP endpoint, which (like the real one) refuses a
// request without the session's X-Agent-Token, and GET /state.tgz serving the archive under
// test, which is where the entrypoint restores a resumed session's state from. Every other
// callback gets 200 {}.
import fs from 'node:fs';
import http from 'node:http';

const PORT = Number(process.env.HUB_PORT || 18093);
const TOKEN = process.env.EXPECTED_TOKEN;
http.createServer((req, res) => {
  let body = '';
  req.on('data', chunk => { body += chunk; });
  req.on('end', () => {
    if (req.method === 'GET' && req.url === '/state.tgz') {
      res.writeHead(200);
      return res.end(fs.readFileSync(process.env.STATE_FILE));
    }
    if (req.method === 'POST' && req.url.endsWith('/mcp')) {
      const tokenOk = req.headers['x-agent-token'] === TOKEN;
      let rpc = {};
      try { rpc = JSON.parse(body); } catch {}
      console.log('[hub] ' + JSON.stringify({ url: req.url, method: rpc.method, tokenOk }));
      if (!tokenOk) { res.writeHead(401); return res.end(); }
      if (rpc.id === undefined) { res.writeHead(202); return res.end(); }
      const result = rpc.method === 'initialize'
        ? { protocolVersion: rpc.params?.protocolVersion || '2025-06-18', capabilities: { tools: {} },
            serverInfo: { name: 'stand-in-skill-library', version: '0' } }
        : rpc.method === 'tools/list'
          ? { tools: [{ name: 'search_skills', inputSchema: { type: 'object', properties: {} } }] }
          : {};
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ jsonrpc: '2.0', id: rpc.id, result }));
    }
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end('{}');
  });
}).listen(PORT, '127.0.0.1', () => console.log('[hub] listening on ' + PORT));
