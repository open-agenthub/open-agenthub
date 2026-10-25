// Real pinned CLI, production driver/server/hooks and MCP; the hub/model are local fixtures.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { execFileSync } = require('node:child_process');
const { createRequire } = require('node:module');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const deps = createRequire(process.env.PROOF_DEPS + '/package.json');
const runtimeDeps = createRequire('/opt/session-agent/package.json');
const { Terminal } = deps('@xterm/headless');
const WebSocket = runtimeDeps('ws');
const run = fs.mkdtempSync('/tmp/follow251-');
const home = run + '/home', cwd = run + '/work';
fs.mkdirSync(home + '/.claude', { recursive: true }); fs.mkdirSync(cwd);
const key = 'proof-key-251';
fs.writeFileSync(home + '/.claude.json', JSON.stringify({ hasCompletedOnboarding: true, customApiKeyResponses: { approved: [key], rejected: [] }, projects: { [cwd]: { hasTrustDialogAccepted: true } } }));
let server, ws, stage = 0, scheduledAt = 0, finalAt = 0;
const requests = [];
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, label) { const end = Date.now() + 90000; while (Date.now() < end) { if (await fn()) return; await sleep(150); } throw Error('timeout ' + label); }
const hub = http.createServer(async (req, res) => {
 let raw = ''; for await (const c of req) raw += c;
 let body = {}; try { body = raw ? JSON.parse(raw) : {}; } catch {}
 const json = value => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(value)); };
 if (req.url.startsWith('/v1/messages') && !req.url.includes('count_tokens') && body.stream) {
  requests.push(body); fs.writeFileSync(run + '/requests.json', JSON.stringify(requests, null, 2));
  const messages = JSON.stringify(body.messages);
  const isMain = body.tools?.length > 0 && /OLD-MARKER-251|context handover|LATER-TURN-251/.test(messages);
  res.writeHead(200, { 'Content-Type': 'text/event-stream' });
  const ev = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
  ev('message_start', { message: { id: 'proof' + requests.length, type: 'message', role: 'assistant', model: body.model, content: [], stop_reason: null, usage: { input_tokens: 10, output_tokens: 1 } } });
  const tool = isMain && stage === 0 && body.tools?.find(t => t.name.includes('context_clear'));
  if (tool) {
   stage = 1;
   ev('content_block_start', { index: 0, content_block: { type: 'tool_use', id: 'clear251', name: tool.name, input: {} } });
   ev('content_block_delta', { index: 0, delta: { type: 'input_json_delta', partial_json: JSON.stringify({ followUp: 'Resume task HANDOVER-251.' }) } });
  } else {
   if (isMain && stage === 1) { stage = 2; await sleep(4000); finalAt = Date.now(); }
   ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
   ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'ANSWER-251-' + (messages.includes('LATER-TURN-251') ? 'later' : messages.includes('context handover') ? 'handover' : 'old') } });
  }
  ev('content_block_stop', { index: 0 }); ev('message_delta', { delta: { stop_reason: tool ? 'tool_use' : 'end_turn' }, usage: { output_tokens: 20 } }); ev('message_stop', {}); res.end(); return;
 }
 if (req.url.startsWith('/v1/')) return json({ input_tokens: 10, content: [{ type: 'text', text: 'Title' }], role: 'assistant', stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } });
 if (req.url.endsWith('/context-clear')) {
  const response = await fetch('http://127.0.0.1:19252/internal/sessions/s251/context-clear', { method: 'POST', headers: { 'X-Agent-Token': 'proof251', 'Content-Type': 'application/json' }, body: raw });
  scheduledAt = Date.now(); return json(await response.json());
 }
 if (req.url.includes('/permission')) return json({ decision: 'allow', id: 'p251' });
 if (req.url.includes('/messages')) return json({ messages: [] });
 return json({ allowed: true });
});
(async () => {
 await new Promise(r => hub.listen(19250, '127.0.0.1', r));
 const env = { PATH: process.env.PROOF_DEPS + '/node_modules/.bin:/usr/local/bin:/usr/bin:/bin', HOME: home, TMPDIR: run, TERM: 'xterm-256color', LANG: 'C.UTF-8', ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:19250', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1', AGENTHUB_SESSION_ID: 's251', AGENTHUB_PORT: '19251', AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:19250/internal/sessions/s251', AGENTHUB_CALLBACK_TOKEN: 'proof251', AGENTHUB_WORKDIR: cwd, AGENTHUB_MODE: 'interactive', AGENTHUB_SKILLS_SYNC_SECONDS: '0', AGENTHUB_CLAUDE_SESSION_ID: require('node:crypto').randomUUID() };
 const settings = JSON.parse(execFileSync('bash', [root + '/agent-runtime/claude/hooks/mcp-policy-hook.sh', '--settings'], { env: { ...env, AGENTHUB_RUNTIME: root + '/agent-runtime/claude/hooks', RUNTIME: root + '/agent-runtime' }, encoding: 'utf8' }));
 settings.permissions = { allow: ['mcp__agenthub_sessions__context_clear'] };
 fs.writeFileSync(home + '/.claude/settings.json', JSON.stringify(settings));
 const mcp = run + '/mcp.json';
 fs.writeFileSync(mcp, JSON.stringify({ mcpServers: { agenthub_sessions: { command: 'node', args: [root + '/agent-runtime/sessions/server.mjs'] } } })); env.AGENTHUB_MCP_CONFIG = mcp;
 const driver = require(root + '/agent-runtime/claude/driver');
 server = require(root + '/agent-runtime/common/server').createCommonServer({ env, driver, dependencies: { pty: runtimeDeps('node-pty'), WebSocketServer: WebSocket.WebSocketServer } });
 const term = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
 ws = new WebSocket('ws://127.0.0.1:19251/'); await new Promise(r => ws.on('open', r));
 const input = data => ws.send(JSON.stringify({ type: 'input', data }));
 term.onData(input); ws.on('message', d => { term.write(d.toString()); fs.writeFileSync(run + '/screen-latest.txt', screen()); });
 const screen = () => Array.from({ length: term.rows }, (_, i) => term.buffer.active.getLine(term.buffer.active.baseY + i)?.translateToString(true)).join('\n');
 await until(() => { fs.writeFileSync(run + '/screen.txt', screen()); return /for shortcuts|❯/.test(screen()); }, 'ready'); await sleep(3000);
 async function paste(text) { input('\x1b[200~' + text + '\x1b[201~'); await sleep(200); input('\r'); }
 const read = async from => { const r = await fetch('http://127.0.0.1:19251/agenthub/transcript?from=' + from, { headers: { 'X-Agent-Token': 'proof251' } }); return { text: await r.text(), next: Number(r.headers.get('X-Transcript-Next')), id: r.headers.get('X-Transcript-Id') }; };
 const samples = [];
 const poll = setInterval(async () => { try { const page = await (await fetch('http://127.0.0.1:19252/conversation')).json(); samples.push(page); fs.writeFileSync(run + '/pages.json', JSON.stringify(samples)); } catch {} }, 300);
 await paste('OLD-MARKER-251: clear your context with the tool, then continue.');
 await until(() => requests.some(r => JSON.stringify(r.messages).includes('[AgentHub context handover]')), 'handover');
 await sleep(2000);
 const initial = await read(0); fs.writeFileSync(run + '/initial.jsonl', initial.text);
 assert(initial.text.includes('HANDOVER-251')); assert(scheduledAt && finalAt >= scheduledAt + 3900);
 const fresh = requests.find(r => JSON.stringify(r.messages).includes('[AgentHub context handover]') && !JSON.stringify(r.messages).includes('clear251'));
 assert(fresh && !JSON.stringify(fresh.messages).includes('OLD-MARKER-251'));
 await paste('LATER-TURN-251');
 await until(() => requests.some(r => JSON.stringify(r.messages).includes('LATER-TURN-251')), 'later model request'); await sleep(3000);
 const later = await read(initial.next); fs.writeFileSync(run + '/later.jsonl', later.text);
 assert(later.text.includes('LATER-TURN-251')); assert(later.text.includes('ANSWER-251-later'));
 for (let i = 2; i <= 3; i++) { await paste('LATER-TURN-251-' + i); await sleep(3500); }
 await until(() => samples.some(p => p.entries?.some(e => e.text.includes('LATER-TURN-251-3'))), 'backend later turn');
 const stored = await (await fetch('http://127.0.0.1:19252/stored')).json();
 assert(stored.entries.some(e => e.text.includes('LATER-TURN-251-3')));
 clearInterval(poll);
 console.log(JSON.stringify({ run, scheduledAt, finalAt, first: initial.next, later: later.next, liveFollow: true }));
 process.exit(0);
})().catch(e => { console.error(run, e); process.exit(1); });
