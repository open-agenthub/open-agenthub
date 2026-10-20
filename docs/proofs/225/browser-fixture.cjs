// Serve the production bundle with synthetic conversation data so the proof exercises
// the actual markdown view without needing credentials or a live model session.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { WebSocketServer } = require('../../../agent-runtime/session-agent/node_modules/ws');
const root = path.resolve(__dirname, '../../../frontend/dist');
const session = { id: 'diagram-proof', title: 'Diagram compatibility proof', phase: 'Succeeded', agent: 'Codex', mode: 'Autonomous', uiMode: 'terminal', accessRole: 'Owner', createdAt: new Date().toISOString(), browser: { phase: 'Stopped' } };
const text = '```mermaid\nflowchart TD\n A[Request] --> B[Response]\n```\n\n```mermaid\nsequenceDiagram\n participant A as Client\n participant B as Server\n A->>B: Request\n B-->>A: Response\n```';
const entries = [{ role: 'assistant', text }];
const server = http.createServer((req, res) => {
  const p = new URL(req.url, 'http://example.com').pathname;
  if (p.startsWith('/api/')) {
    let value = [];
    if (p === '/api/config') value = { authority: '', version: 'diagram-proof' };
    else if (p === '/api/sessions') value = [session];
    else if (p === '/api/sessions/diagram-proof') value = session;
    else if (p.endsWith('/conversation')) value = { source: 'native', offset: 0, nextOffset: 1, length: 1, entries };
    else if (p.endsWith('/files/presentation')) value = { revision: 0, fileId: null };
    else if (p.endsWith('/files/capabilities')) value = { canUpload: false, canRead: true, limits: { presentationPollMilliseconds: 30000 } };
    else if (p === '/api/agents/allowed') value = { agents: ['Codex'] };
    else if (p === '/api/admin/access') value = { isAdmin: false };
    else if (p === '/api/credentials' || p === '/api/credentials/accounts') value = {};
    res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); return;
  }
  const name = p.startsWith('/assets/') || p === '/favicon.svg' ? p : '/index.html';
  const file = path.resolve(root, '.' + name);
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
  const types = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
});
new WebSocketServer({ server }).on('connection', () => {});
server.listen(Number(process.env.PORT || 5173), '0.0.0.0');
