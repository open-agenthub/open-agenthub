// #305 proof: the REAL claude/entrypoint.sh and common/server.js from this checkout, with the
// pinned Claude Code, decide from the pod's credentials whether the session signs in by itself.
//
//   node entrypoint-sign-in.cjs <none|apikey|subscription|customendpoint|cancel|login> [--serve]
//
// The runtime is copied into the image layout and only paths are relocated: /opt/session-agent,
// /tmp and /secrets. /secrets matters most: in a session pod it holds that session's real login,
// which the shipped entrypoint would otherwise copy. Credentials here are fake or absent.
// Screens are printed with sign-in addresses removed; nothing is written outside ROOT, which is
// deleted at the end unless --serve keeps it running for the browser (Ctrl+C ends it).
'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const { spawn, execFileSync } = require('node:child_process');
const modules = '/opt/session-agent/node_modules';
const { WebSocket, WebSocketServer } = require(modules + '/ws');
const { Terminal } = require(modules + '/@xterm/headless');

const scenario = process.argv[2] || 'none';
const serve = process.argv.includes('--serve');
const repo = path.resolve(__dirname, '../../..');
const ROOT = fs.mkdtempSync(path.join(os.tmpdir(), 'proof-305-'));
const AGENT_PORT = Number(process.env.PROOF_AGENT_PORT || 17705);
const HUB_PORT = Number(process.env.PROOF_HUB_PORT || 8080);
const MODEL_PORT = AGENT_PORT + 1;
const claudeBin = path.dirname(execFileSync('bash', ['-c', 'command -v claude']).toString().trim());
const started = Date.now();
const ms = () => Date.now() - started;

function build() {
  const rt = path.join(ROOT, 'rt');
  fs.mkdirSync(rt, { recursive: true });
  for (const dir of ['common', 'claude', 'files', 'browser', 'sessions', 'network', 'skills']) {
    fs.cpSync(path.join(repo, 'agent-runtime', dir), path.join(rt, dir), { recursive: true, filter: s => !s.includes('node_modules') });
  }
  for (const file of ['server.js', 'package.json']) fs.copyFileSync(path.join(repo, 'agent-runtime/session-agent', file), path.join(rt, file));
  fs.symlinkSync(modules, path.join(rt, 'node_modules'));
  for (const dir of ['tmp', 'secrets/claude', 'secrets/creds', 'secrets/gitcreds', 'secrets/mcp', 'home', 'work']) fs.mkdirSync(path.join(ROOT, dir), { recursive: true });
  const walk = dir => fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isSymbolicLink() ? [] : e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]);
  for (const file of walk(rt).filter(f => /\.(sh|js|mjs|cjs)$/.test(f))) {
    const source = fs.readFileSync(file, 'utf8');
    const moved = source
      .replaceAll('/opt/session-agent', rt)
      .replaceAll('/opt/agenthub/', path.join(ROOT, 'absent-opt-agenthub') + '/')
      .replace(/(["' =(])\/secrets\//g, `$1${ROOT}/secrets/`)
      .replace(/(["' =])\/tmp\//g, `$1${ROOT}/tmp/`);
    if (moved !== source) fs.writeFileSync(file, moved);
  }
  for (const file of walk(rt).filter(f => f.endsWith('.sh'))) fs.chmodSync(file, 0o755);
  return rt;
}

// Stand-in hub: callbacks answer {} and are counted by path only; the web app gets one session
// and its terminal socket is proxied to the session agent, as the backend's TerminalProxy does.
const callbacks = [];
// Upload bodies stay in memory and are only searched; nothing of them is printed.
const uploads = [];
const session = { id: 's1', title: 'Sign-in proof', agent: 'Claude', phase: 'Running', status: 'Running', mode: 'Interactive',
  uiMode: 'terminal', accessRole: 'Owner', authMode: 'Auto', createdAt: new Date().toISOString(), repos: [], browser: { phase: 'Stopped' } };
const hub = http.createServer(async (req, res) => {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  const url = req.url.split('?')[0];
  if (req.method === 'PUT') uploads.push({ url, body: Buffer.concat(chunks) });
  if (!url.startsWith('/api/')) callbacks.push(req.method + ' ' + url.replace(/[0-9a-f-]{36}/g, '<id>'));
  const json = v => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(v)); };
  if (url === '/api/config') return json({ authority: '', version: 'proof' });
  if (url === '/api/sessions') return json([session]);
  if (url === '/api/sessions/s1') return json(session);
  if (url === '/api/agents/allowed') return json({ agents: ['Claude'] });
  if (url === '/api/admin/access') return json({ isAdmin: false });
  if (url === '/api/credentials/accounts' || url === '/api/credentials') return json({});
  if (url.endsWith('/conversation')) return json({ source: 'native', entries: [], text: '', offset: 0, nextOffset: 0, length: 0 });
  if (url.endsWith('/files/capabilities')) return json({ canRead: true, canUpload: false, limits: { presentationPollMilliseconds: 30000 } });
  if (url.endsWith('/files/presentation')) return json({ revision: 0, fileId: null });
  if (['/api/projects', '/api/mcp-servers'].includes(url) || /\/(permissions|messages|files|commands)$/.test(url)) return json([]);
  if (!url.startsWith('/api/')) return json({});
  res.writeHead(404); res.end();
});
const wss = new WebSocketServer({ noServer: true, handleProtocols: () => 'tty' });
hub.on('upgrade', (req, socket, head) => wss.handleUpgrade(req, socket, head, client => {
  if (!req.url.startsWith('/ws/sessions/s1/terminal')) return client.close();
  const upstream = new WebSocket(`ws://127.0.0.1:${AGENT_PORT}/`, 'tty');
  const pending = [];
  upstream.on('open', () => { for (const m of pending.splice(0)) upstream.send(m); });
  upstream.on('message', (d, binary) => client.readyState === 1 && client.send(binary ? d : d.toString()));
  client.on('message', d => upstream.readyState === 1 ? upstream.send(d.toString()) : pending.push(d.toString()));
  client.on('close', () => upstream.close());
  upstream.on('close', () => client.close());
  // Before the agent listens; the web app reconnects, as it does to a starting pod.
  upstream.on('error', () => client.close());
}));

// A Messages endpoint for the API-key and custom-endpoint starts; nothing is asked of it unless
// a turn runs.
const model = http.createServer(async (req, res) => {
  for await (const _ of req) {}
  res.writeHead(200, { 'content-type': 'application/json' });
  res.end(JSON.stringify({ id: 'msg_proof', type: 'message', role: 'assistant', model: 'proof-model', content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } }));
});

function podEnv(rt) {
  // The variables AgentPodSpecFactory.Build gives a Claude container (backend/Services/
  // AgentPodSpecFactory.cs), with fieldRefs and secretKeyRefs filled in as the kubelet would.
  const hubUrl = `http://127.0.0.1:${HUB_PORT}`;
  const env = {
    PATH: `${claudeBin}:/usr/local/bin:/usr/bin:/bin`, HOME: path.join(ROOT, 'home'),
    AGENTHUB_MODE: 'interactive', AGENTHUB_UI_MODE: 'terminal', AGENTHUB_AUTH_MODE: 'auto',
    AGENTHUB_SESSION_ID: 's1', AGENTHUB_CLAUDE_SESSION_ID: crypto.randomUUID(), AGENTHUB_PORT: String(AGENT_PORT),
    AGENTHUB_POD_IP: '127.0.0.1', AGENTHUB_HAS_REPO: '0', AGENTHUB_WORKDIR: path.join(ROOT, 'work'), AGENTHUB_HAS_MCP: '0',
    AGENTHUB_BROWSER_ENABLED: '0', AGENTHUB_SPAWN_MCP_ENABLED: '0', AGENTHUB_NETWORK_MCP_ENABLED: '0', AGENTHUB_CONTAINERS_ENABLED: '0',
    AGENTHUB_RESUME: '0', AGENTHUB_PROMPT: '', AGENTHUB_SYSTEM_PROMPT: '', AGENTHUB_AUTO_APPROVE: '0',
    AGENTHUB_ALLOWED_TOOLS: '[]', AGENTHUB_ALLOWED_MCP_TOOLS: '[]', AGENTHUB_ALLOWED_COMMANDS: '[]',
    AGENTHUB_CALLBACK_URL: hubUrl + '/internal', AGENTHUB_CALLBACK_TOKEN: 'example-callback-token',
    AGENTHUB_LIMIT_THRESHOLD: '90', AGENTHUB_S3_INSECURE: '0',
    AGENTHUB_STATE_PUT_URL: hubUrl + '/upload/state', AGENTHUB_STATE_GET_URL: '',
    AGENTHUB_SCROLLBACK_PUT_URL: hubUrl + '/upload/scrollback', AGENTHUB_TRANSCRIPT_PUT_URL: hubUrl + '/upload/transcript',
    TERMINATION_MESSAGE_PATH: path.join(ROOT, 'termination-log'),
    // Proof only: nothing reaches the provider. The fake identity proxy (mock-auth.cjs from #242)
    // completes a login when PROOF_TLS_CA is given.
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1'
  };
  if (process.env.PROOF_TLS_CA) Object.assign(env, { HTTPS_PROXY: 'http://127.0.0.1:18443', NODE_EXTRA_CA_CERTS: process.env.PROOF_TLS_CA });
  if (scenario === 'apikey') Object.assign(env, { AGENTHUB_AUTH_MODE: 'apikey', ANTHROPIC_API_KEY: 'sk-ant-api03-' + 'x'.repeat(80), ANTHROPIC_BASE_URL: `http://127.0.0.1:${MODEL_PORT}` });
  if (scenario === 'subscription') {
    env.AGENTHUB_AUTH_MODE = 'subscription';
    fs.writeFileSync(path.join(ROOT, 'secrets/claude/credentials.json'), JSON.stringify({ claudeAiOauth: {
      accessToken: 'example-access', refreshToken: 'example-refresh', expiresAt: Date.now() + 3600_000,
      scopes: ['user:inference', 'user:profile'], subscriptionType: 'max' } }));
  }
  if (scenario === 'customendpoint') Object.assign(env, { AGENTHUB_AUTH_MODE: 'customendpoint',
    AGENTHUB_MODEL_ENDPOINT_URL: `http://127.0.0.1:${MODEL_PORT}`, AGENTHUB_MODEL_NAME: 'proof-model', AGENTHUB_MODEL_API_KEY: 'example-endpoint-key' });
  return env;
}

function sanitize(rows) {
  let continuation = false;
  return rows.map(line => {
    if (/https?:\/\//.test(line)) { continuation = true; return line.replace(/https?:\/\/.*$/, '[sign-in URL removed]'); }
    if (continuation && /^\S+$/.test(line.trim())) return '[wrapped sign-in URL removed]';
    continuation = false;
    return line;
  }).filter(Boolean).join('\n');
}

(async () => {
  const busy = await new Promise(resolve => {
    const probe = require('node:net').connect(AGENT_PORT, '127.0.0.1', () => { probe.destroy(); resolve(true); });
    probe.on('error', () => resolve(false));
  });
  if (busy) throw new Error(`port ${AGENT_PORT} is taken: a session agent from an earlier run is still up`);
  const rt = build();
  await new Promise(r => hub.listen(HUB_PORT, '127.0.0.1', r));
  await new Promise(r => model.listen(MODEL_PORT, '127.0.0.1', r));
  const log = [];
  // Its own process group, so the stop below also ends the CLI and the auth watcher; a
  // survivor would keep the port and answer the next scenario's connection.
  const agent = spawn('bash', [path.join(rt, 'claude/entrypoint.sh')], { env: podEnv(rt), cwd: path.join(ROOT, 'work'), stdio: ['ignore', 'pipe', 'pipe'], detached: true });
  for (const stream of [agent.stdout, agent.stderr]) stream.on('data', d => log.push(...d.toString().split('\n').filter(Boolean)));
    const stop = async () => {
    try { process.kill(-agent.pid, 'SIGTERM'); } catch {}
    await new Promise(r => setTimeout(r, 1500));
    try { process.kill(-agent.pid, 'SIGKILL'); } catch {}
    hub.close(); model.close();
    fs.rmSync(ROOT, { recursive: true, force: true });
  };
  if (serve) {
    console.log(JSON.stringify({ scenario, serving: `hub http://127.0.0.1:${HUB_PORT}`, agentPort: AGENT_PORT }));
    process.on('SIGINT', () => stop().then(() => process.exit(0)));
    process.on('SIGTERM', () => stop().then(() => process.exit(0)));
    return;
  }

  const terminal = new Terminal({ cols: 120, rows: 40, allowProposedApi: true });
  let socket;
  for (let i = 0; i < 100 && !socket; i++) {
    await new Promise(r => setTimeout(r, 200));
    socket = await new Promise(resolve => {
      const ws = new WebSocket(`ws://127.0.0.1:${AGENT_PORT}/`, 'tty');
      ws.once('open', () => resolve(ws));
      ws.once('error', () => resolve(null));
    });
  }
  if (!socket) throw new Error('session agent did not listen; entrypoint log:\n' + log.join('\n'));
  const connectedAt = ms();
  socket.on('message', (data, binary) => {
    const text = data.toString();
    if (!binary && text.startsWith('{"type":"agenthub"')) return;
    terminal.write(text);
  });
  socket.send(JSON.stringify({ type: 'resize', cols: 120, rows: 40 }));
  const rows = () => { const b = terminal.buffer.active; return Array.from({ length: terminal.rows }, (_, y) => b.getLine(b.baseY + y)?.translateToString(true) || ''); };
  const send = data => socket.send(JSON.stringify({ type: 'input', data }));
  const waitFor = async (pattern, timeout = 30000) => {
    const until = Date.now() + timeout;
    while (Date.now() < until) { if (pattern.test(rows().join('\n'))) return ms(); await new Promise(r => setTimeout(r, 100)); }
    return null;
  };
  const result = { scenario, connectedAt };
  const loginScreen = /Paste code here if prompted/;
  const tui = /^❯/m;
  result.loginShownAt = await waitFor(loginScreen, scenario === 'none' || scenario === 'cancel' || scenario === 'login' ? 30000 : 15000);
  result.tuiShownAt = await waitFor(tui, 1000);
  result.typedAnything = false;
  if (scenario === 'cancel' && result.loginShownAt) {
    send('\x03');
    result.tuiAfterCancelAt = await waitFor(tui, 30000);
  }
  if (scenario === 'login' && result.loginShownAt) {
    // The same bytes the Workspace card sends: one bracketed paste, then Enter.
    const screen = rows();
    let address = '';
    for (let i = 0; i < screen.length; i++) {
      const match = /https:\/\/\S+/.exec(screen[i]); if (!match) continue;
      address = match[0]; while (i + 1 < screen.length && /^\S+$/.test(screen[i + 1].trim())) address += screen[++i].trim();
    }
    const state = new URL(address).searchParams.get('state');
    send('\x1b[200~example-code#' + state + '\x1b[201~'); await new Promise(r => setTimeout(r, 150)); send('\r');
    result.loginSucceededAt = await waitFor(/Login successful\./, 30000);
    result.tuiAfterLoginAt = await waitFor(tui, 30000);
    result.credentialsWritten = fs.existsSync(path.join(ROOT, 'home/.claude/.credentials.json'));
    // The shipped watcher hands a login made in the session to the hub as the owner's account.
    for (let i = 0; i < 90 && !callbacks.some(c => c.endsWith('/claude-credentials')); i++) await new Promise(r => setTimeout(r, 500));
    result.loginUploadedToHub = callbacks.some(c => c.endsWith('/claude-credentials'));
    // The session agent's periodic upload (30 s) after the login: scrollback and state must not
    // carry the sign-in address or its state value (docs/proofs/242-cli-sign-in.md).
    const before = uploads.length;
    for (let i = 0; i < 90 && !uploads.slice(before).some(u => u.url.endsWith('/scrollback')); i++) await new Promise(r => setTimeout(r, 500));
    const text = uploads.map(u => u.url.endsWith('/state') ? require('node:zlib').gunzipSync(u.body).toString('latin1') : u.body.toString('utf8'));
    result.uploadsAfterLogin = uploads.slice(before).map(u => u.url);
    result.signInDataInUploads = text.some(t => t.includes(state) || t.includes(address) || t.includes('example-code#'));
    result.scrollbackMarksOmission = text.some(t => t.includes('[Sign-in terminal output omitted]'));
  }
  await new Promise(r => setTimeout(r, 1000));
  result.launch = (log.find(l => l.startsWith('[agent] driver=')) || '').replace(/ cwd=\S+/, '').replace(ROOT, '<root>').replaceAll(ROOT, '<root>');
  result.screen = sanitize(rows());
  result.callbacks = [...new Set(callbacks)];
  console.log(JSON.stringify(result, null, 1));
  socket.close();
  await stop();
  process.exit(0);
})().catch(async error => { console.error(error.message); fs.rmSync(ROOT, { recursive: true, force: true }); process.exit(1); });
