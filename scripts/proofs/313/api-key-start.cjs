// #313 proof, built on the #306 rig: an API-key Claude session starts without the CLI's "Do you
// want to use this API key?" question, the first request carries the key as x-api-key, and no
// key material reaches transcripts or uploads.
//   node api-key-start.cjs apikey
// (The other scenarios of the #306 rig are kept.) Below, the #306 description:
//
// #306 measurement: where a Claude session's start-up time goes, with the REAL
// claude/entrypoint.sh, common/server.js and pinned Claude Code from this checkout.
//
//   node startup-timeline.cjs <scenario> [--early-input] [--serve]
//     scenario: subscription  fake OAuth login mounted, the CLI talks to the real network
//               mock          fake OAuth login, all HTTPS through the local fake identity
//                             (scripts/proofs/242/mock-auth.cjs; PROOF_TLS_CA=<its CA file>)
//               apikey        API key against a local Messages endpoint, key pre-approved
//               none          nothing to authenticate with
//     --early-input  types a marker the moment the agent socket opens and reports when the
//                    CLI shows it in its prompt
//     --serve        keeps the rig up behind a stand-in hub on :8080 for the browser
//
// Times are milliseconds from the entrypoint's start, as the pod's container start would be.
// Only paths are relocated (/opt/session-agent, /tmp, /secrets); in a session pod /secrets
// holds that session's real login, which the shipped entrypoint would otherwise copy.
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

const scenario = process.argv[2] || 'subscription';
const serve = process.argv.includes('--serve');
const earlyInput = process.argv.includes('--early-input');
const repo = path.resolve(__dirname, '../../..');
const ROOT = fs.mkdtempSync(path.join(os.tmpdir(), 'proof-306-'));
const AGENT_PORT = Number(process.env.PROOF_AGENT_PORT || 17715);
const HUB_PORT = Number(process.env.PROOF_HUB_PORT || 8080);
const MODEL_PORT = AGENT_PORT + 1;
const claudeBin = path.dirname(execFileSync('bash', ['-c', 'command -v claude']).toString().trim());
let started = Date.now();
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
  // Measurement only: a SessionStart hook that records when the CLI fires it.
  const hooks = path.join(rt, 'claude/hooks/mcp-policy-hook.sh');
  fs.writeFileSync(hooks, fs.readFileSync(hooks, 'utf8').replace('  "hooks": {\n',
    `  "hooks": {\n    "SessionStart": [ { "hooks": [ { "type": "command", "command": "date +%s%3N >> ${ROOT}/session-start" } ] } ],\n`));
  return rt;
}

const callbacks = [];
// A fake key with a recognisable, non-repeating tail, so a leaked suffix is found by search.
const API_KEY = 'sk-ant-api03-example-' + crypto.randomBytes(30).toString('hex');
const modelRequests = [];
// Upload bodies stay in memory and are only searched; nothing of them is printed.
const uploads = [];
const session = { id: 's1', title: 'Start-up proof', agent: 'Claude', phase: 'Running', status: 'Running', mode: 'Interactive',
  uiMode: 'terminal', accessRole: 'Owner', authMode: 'Subscription', createdAt: new Date().toISOString(), repos: [], browser: { phase: 'Stopped' } };
const hub = http.createServer(async (req, res) => {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  const url = req.url.split('?')[0];
  if (!url.startsWith('/api/')) uploads.push({ url, body: Buffer.concat(chunks) });
  if (!url.startsWith('/api/')) callbacks.push({ at: ms(), call: req.method + ' ' + url });
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

// A Messages endpoint that answers every turn with one short text.
const model = http.createServer(async (req, res) => {
  let body = '';
  for await (const chunk of req) body += chunk;
  let json = {}; try { json = JSON.parse(body); } catch {}
  if (req.url.startsWith('/v1/messages') && !req.url.includes('count_tokens')) modelRequests.push({
    xApiKeyIsSessionKey: req.headers['x-api-key'] === API_KEY, authorization: req.headers.authorization !== undefined });
  if (serve && req.url.startsWith('/v1/messages') && body.includes('say done')) console.log(JSON.stringify({ modelGotTask: true }));
  const msg = { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: json.model || 'proof-model', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } };
  if (!req.url.startsWith('/v1/messages') || req.url.includes('count_tokens') || !json.stream) {
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end(JSON.stringify(req.url.includes('count_tokens') ? { input_tokens: 10 } : { ...msg, content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn' }));
  }
  res.writeHead(200, { 'content-type': 'text/event-stream' });
  const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n');
  ev('message_start', { message: msg });
  ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
  ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'done' } });
  ev('content_block_stop', { index: 0 });
  ev('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } });
  ev('message_stop', {});
  res.end();
});

function podEnv() {
  // The variables AgentPodSpecFactory.Build gives a Claude container, fieldRefs and
  // secretKeyRefs filled in as the kubelet would.
  const hubUrl = `http://127.0.0.1:${HUB_PORT}`;
  const env = {
    PATH: `${claudeBin}:/usr/local/bin:/usr/bin:/bin`, HOME: path.join(ROOT, 'home'),
    AGENTHUB_MODE: 'interactive', AGENTHUB_UI_MODE: 'terminal', AGENTHUB_AUTH_MODE: 'subscription',
    AGENTHUB_SESSION_ID: 's1', AGENTHUB_CLAUDE_SESSION_ID: crypto.randomUUID(), AGENTHUB_PORT: String(AGENT_PORT),
    AGENTHUB_POD_IP: '127.0.0.1', AGENTHUB_HAS_REPO: '0', AGENTHUB_WORKDIR: path.join(ROOT, 'work'), AGENTHUB_HAS_MCP: '0',
    AGENTHUB_BROWSER_ENABLED: '0', AGENTHUB_SPAWN_MCP_ENABLED: '0', AGENTHUB_NETWORK_MCP_ENABLED: '0', AGENTHUB_CONTAINERS_ENABLED: '0',
    AGENTHUB_RESUME: '0', AGENTHUB_PROMPT: process.env.PROOF_PROMPT || '', AGENTHUB_SYSTEM_PROMPT: '', AGENTHUB_AUTO_APPROVE: '0',
    AGENTHUB_ALLOWED_TOOLS: '[]', AGENTHUB_ALLOWED_MCP_TOOLS: '[]', AGENTHUB_ALLOWED_COMMANDS: '[]',
    AGENTHUB_CALLBACK_URL: hubUrl + '/internal', AGENTHUB_CALLBACK_TOKEN: 'example-callback-token',
    AGENTHUB_LIMIT_THRESHOLD: '90', AGENTHUB_S3_INSECURE: '0',
    AGENTHUB_STATE_PUT_URL: hubUrl + '/upload/state', AGENTHUB_STATE_GET_URL: '',
    AGENTHUB_SCROLLBACK_PUT_URL: hubUrl + '/upload/scrollback', AGENTHUB_TRANSCRIPT_PUT_URL: hubUrl + '/upload/transcript',
    TERMINATION_MESSAGE_PATH: path.join(ROOT, 'termination-log')
  };
  const oauth = () => fs.writeFileSync(path.join(ROOT, 'secrets/claude/credentials.json'), JSON.stringify({ claudeAiOauth: {
    accessToken: 'example-access', refreshToken: 'example-refresh', expiresAt: Date.now() + 3600_000,
    scopes: ['user:inference', 'user:profile'], subscriptionType: 'max' } }));
  if (scenario === 'subscription') oauth();
  if (scenario === 'mock') {
    if (!process.env.PROOF_TLS_CA) throw new Error('mock needs PROOF_TLS_CA from scripts/proofs/242/mock-auth.cjs');
    oauth();
    Object.assign(env, { HTTPS_PROXY: 'http://127.0.0.1:18443', NODE_EXTRA_CA_CERTS: process.env.PROOF_TLS_CA });
  }
  if (scenario === 'apikey') {
    const key = API_KEY;
    Object.assign(env, { AGENTHUB_AUTH_MODE: 'apikey', ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: `http://127.0.0.1:${MODEL_PORT}` });
    // Nothing pre-approved here: approving the key is what the entrypoint is under test for.
  }
  if (scenario === 'none') env.AGENTHUB_AUTH_MODE = 'auto';
  return env;
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
  const env = podEnv();
  // --serve: the web app can be open before the "container" starts, as when a session is created.
  if (serve && process.env.PROOF_DELAY_AGENT_MS) {
    console.log(JSON.stringify({ scenario, serving: `hub http://127.0.0.1:${HUB_PORT}`, agentPort: AGENT_PORT, agentStartsInMs: Number(process.env.PROOF_DELAY_AGENT_MS) }));
    await new Promise(r => setTimeout(r, Number(process.env.PROOF_DELAY_AGENT_MS)));
  }
  started = Date.now();
  // Its own process group, so the stop below also ends the CLI and the auth watcher.
  const agent = spawn('bash', [path.join(rt, 'claude/entrypoint.sh')], { env, cwd: path.join(ROOT, 'work'), stdio: ['ignore', 'pipe', 'pipe'], detached: true });
  for (const stream of [agent.stdout, agent.stderr]) stream.on('data', d => {
    for (const line of d.toString().split('\n').filter(Boolean)) log.push({ at: ms(), line: line.replaceAll(ROOT, '<root>') });
  });
  const stop = async () => {
    try { process.kill(-agent.pid, 'SIGTERM'); } catch {}
    await new Promise(r => setTimeout(r, 1500));
    try { process.kill(-agent.pid, 'SIGKILL'); } catch {}
    // The entrypoint starts the auth watcher in the background; it was seen to outlive the group.
    try { process.kill(Number(fs.readFileSync(path.join(ROOT, 'tmp/agenthub-auth-watcher.pid'), 'utf8')), 'SIGKILL'); } catch {}
    hub.close(); model.close();
    fs.rmSync(ROOT, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  };
  if (serve) {
    if (!process.env.PROOF_DELAY_AGENT_MS) console.log(JSON.stringify({ scenario, serving: `hub http://127.0.0.1:${HUB_PORT}`, agentPort: AGENT_PORT }));
    console.log(JSON.stringify({ agentStarted: true }));
    process.on('SIGINT', () => stop().then(() => process.exit(0)));
    process.on('SIGTERM', () => stop().then(() => process.exit(0)));
    return;
  }

  const marks = {};
  const mark = (name, at = ms()) => { if (marks[name] === undefined) marks[name] = at; };
  const terminal = new Terminal({ cols: 120, rows: 40, allowProposedApi: true });
  const activity = [];
  let socket;
  while (!socket && ms() < 60000) {
    socket = await new Promise(resolve => {
      const ws = new WebSocket(`ws://127.0.0.1:${AGENT_PORT}/`, 'tty');
      ws.once('open', () => resolve(ws));
      ws.once('error', () => resolve(null));
    });
    if (!socket) await new Promise(r => setTimeout(r, 50));
  }
  if (!socket) throw new Error('session agent did not listen; entrypoint log:\n' + log.map(l => l.line).join('\n'));
  mark('agentListening');
  socket.on('message', (data, binary) => {
    const text = data.toString();
    if (!binary && text.startsWith('{"type":"agenthub"')) {
      const frame = JSON.parse(text);
      if (frame.subtype === 'activity' && activity.at(-1)?.state !== frame.state) activity.push({ at: ms(), state: frame.state, ...(frame.ready === undefined ? {} : { ready: frame.ready }) });
      return;
    }
    mark('firstOutput');
    if (inputAt === 'output') sendMarker();
    terminal.write(text);
  });
  socket.send(JSON.stringify({ type: 'subscribe', channel: 'activity' }));
  socket.send(JSON.stringify({ type: 'resize', cols: 120, rows: 40 }));
  const marker = 'early-marker-306';
  // PROOF_INPUT_AT: 'connect' (default), 'output' (first CLI byte), 'prompt' (input box drawn),
  // or a delay in ms after the socket opened.
  const inputAt = process.env.PROOF_INPUT_AT || 'connect';
  let inputSent = false;
  const sendMarker = () => { if (earlyInput && !inputSent) { inputSent = true; mark('inputSent'); socket.send(JSON.stringify({ type: 'input', data: marker })); } };
  if (inputAt === 'connect') sendMarker();
  else if (/^\d+$/.test(inputAt)) setTimeout(sendMarker, Number(inputAt));
  const screen = () => { const b = terminal.buffer.active; return Array.from({ length: terminal.rows }, (_, y) => b.getLine(b.baseY + y)?.translateToString(true) || '').join('\n'); };
  const until = Date.now() + Number(process.env.PROOF_WATCH_MS || 30000);
  while (Date.now() < until) {
    const text = screen();
    if (/Claude Code v\d/.test(text)) mark('banner');
    // The input box: the prompt row between the two rules the TUI draws around it.
    if (/─{20,}\n❯/.test(text)) { mark('promptDrawn'); if (inputAt === 'prompt') sendMarker(); }
    if (earlyInput && text.includes('❯ ' + marker)) mark('earlyInputShown');
    if (marks.promptDrawn !== undefined && (!earlyInput || marks.earlyInputShown !== undefined) && ms() - marks.promptDrawn > 3000) break;
    await new Promise(r => setTimeout(r, 25));
  }
  const keyQuestion = /Do you want to use this API key/;
  const screenBefore = screen();
  let turn = {};
  if (scenario === 'apikey') {
    // One turn, typed the way the Workspace composer sends it.
    socket.send(JSON.stringify({ type: 'input', data: '\x1b[200~say done\x1b[201~' }));
    await new Promise(r => setTimeout(r, 200));
    socket.send(JSON.stringify({ type: 'input', data: '\r' }));
    const end = Date.now() + 30000;
    while (Date.now() < end && !/⏺ done|● done/.test(screen())) await new Promise(r => setTimeout(r, 100));
    turn.answered = /⏺ done|● done/.test(screen());
    // The session agent's periodic upload (30 s) of scrollback, transcript and state.
    const wanted = ['/upload/scrollback', '/upload/state', '/upload/transcript'];
    const end2 = Date.now() + 75000;
    while (Date.now() < end2 && !wanted.every(w => uploads.some(u => u.url === w))) await new Promise(r => setTimeout(r, 500));
    const suffix = API_KEY.slice(-20);
    const texts = uploads.map(u => u.url.endsWith('/state') ? require('node:zlib').gunzipSync(u.body).toString('latin1') : u.body.toString('utf8'));
    let transcript = '';
    try { transcript = await fetch(`http://127.0.0.1:${AGENT_PORT}/agenthub/transcript`, { headers: { 'X-Agent-Token': 'example-callback-token' } }).then(r => r.text()); } catch {}
    const home = path.join(ROOT, 'home');
    const claudeDir = fs.readdirSync(path.join(home, '.claude'), { recursive: true }).map(f => path.join(home, '.claude', f)).filter(f => { try { return fs.statSync(f).isFile(); } catch { return false; } });
    turn = { ...turn,
      uploads: [...new Set(uploads.map(u => u.url))],
      keyInUploads: texts.some(t => t.includes(API_KEY)), suffixInUploads: texts.some(t => t.includes(suffix)),
      keyInLiveTranscript: transcript.includes(API_KEY) || transcript.includes(suffix),
      // ~/.claude is what the state archive carries, minus driver.stateExcludes.
      suffixOnDiskUnderDotClaude: claudeDir.filter(f => fs.readFileSync(f, 'latin1').includes(suffix)).map(f => f.replace(home, '~')),
      approvedInConfig: (JSON.parse(fs.readFileSync(path.join(home, '.claude.json'), 'utf8')).customApiKeyResponses || {}).approved?.includes(suffix),
      keyInEntrypointLog: log.some(l => l.line.includes(API_KEY) || l.line.includes(suffix))
    };
  }
  try {
    const at = Number(fs.readFileSync(path.join(ROOT, 'session-start'), 'utf8').trim().split('\n')[0]);
    if (at) mark('sessionStartHook', at - started);
  } catch {}
  const launch = log.find(l => l.line.startsWith('[agent] driver='));
  if (launch) mark('cliSpawned', launch.at);
  const result = {
    scenario, earlyInput,
    marks: Object.fromEntries(Object.entries(marks).sort((a, b) => a[1] - b[1])),
    activity,
    keyQuestionShown: keyQuestion.test(screenBefore),
    modelRequests,
    turn,
    entrypoint: log.filter(l => l.line.startsWith('[entrypoint]') || l.line.startsWith('[agent]')).map(l => `${l.at} ${l.line.slice(0, 140)}`),
    screenTail: screen().split('\n').filter(Boolean).slice(-4).join('\n')
  };
  console.log(JSON.stringify(result, null, 1));
  socket.close();
  await stop();
  process.exit(0);
})().catch(async error => { console.error(error.message); fs.rmSync(ROOT, { recursive: true, force: true }); process.exit(1); });
