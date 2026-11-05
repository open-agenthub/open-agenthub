// Stand-in for the hub's /internal callback API. Logs every request as one JSON line and answers
// just enough for the session agent and the sessions MCP server: one peer in agents_list, and a
// delivery receipt for POST .../messages.
import http from 'node:http';

const PORT = Number(process.env.HUB_PORT || 18092);
http.createServer((req, res) => {
  let body = '';
  req.on('data', chunk => { body += chunk; });
  req.on('end', () => {
    const tokenOk = req.headers['x-agent-token'] === process.env.EXPECTED_TOKEN ||
      req.headers.authorization === `Bearer ${process.env.EXPECTED_TOKEN}`;
    const short = body.length > 400 ? body.slice(0, 400) + '…' : body;
    console.log('[hub] ' + JSON.stringify({ method: req.method, url: req.url, tokenOk, body: short }));
    let answer = {};
    if (req.method === 'POST' && req.url.endsWith('/mcp')) {
      // The hub's skill-library MCP endpoint, reduced to initialize and one listed tool.
      let rpc = {};
      try { rpc = JSON.parse(body); } catch {}
      if (rpc.id === undefined) { res.writeHead(202); return res.end(); }
      const result = rpc.method === 'initialize'
        ? { protocolVersion: rpc.params?.protocolVersion || '2025-06-18', capabilities: { tools: {} },
            serverInfo: { name: 'stand-in-skill-library', version: '0' } }
        : rpc.method === 'tools/list'
          ? { tools: [{ name: 'search_skills', description: 'stand-in',
              inputSchema: { type: 'object', properties: { query: { type: 'string' } } } }] }
          : {};
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ jsonrpc: '2.0', id: rpc.id, result }));
    }
    if (req.url.endsWith('/project-agents')) {
      answer = [{ id: 'peer-0001', title: 'Peer', phase: 'Running' }];
    } else if (req.method === 'POST' && req.url.endsWith('/messages')) {
      answer = { id: 'msg-0001', to: 'peer-0001', deliveredVia: 'inbox', reason: null };
    } else if (req.url.includes('/permission')) {
      answer = { decision: 'allow' };
    }
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(answer));
  });
}).listen(PORT, '127.0.0.1', () => console.log('[hub] listening on ' + PORT));
