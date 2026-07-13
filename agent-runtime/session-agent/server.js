'use strict';

/**
 * Session agent: main process inside the agent pod.
 * - starts Claude Code under a PTY (with a fixed --session-id or --resume)
 * - buffers the output (scrollback) and serves it via WebSocket (replay on reconnect)
 * - saves Claude state (~/.claude) and scrollback to S3 periodically + on exit (presigned URLs)
 * - reports status (Running/Succeeded/Failed) to the backend
 *
 * Protocol browser <-> agent:
 *   Server -> client: raw terminal output (text)
 *   Client -> server: JSON { type:"input", data } | { type:"resize", cols, rows }
 *
 * Two WebSocket paths on the same port:
 *   /       -> the shared Claude terminal (scrollback replayed on connect)
 *   /shell  -> an interactive `bash -l` shell, spawned on demand per connection
 */

const pty = require('node-pty');
const { WebSocketServer } = require('ws');
const { execFile } = require('child_process');
const fs = require('fs');

const PORT      = parseInt(process.env.AGENTHUB_PORT || '7681', 10);
const MODE      = (process.env.AGENTHUB_MODE || 'interactive').toLowerCase();
const PROMPT    = process.env.AGENTHUB_PROMPT || '';
const ALLOWED   = (process.env.AGENTHUB_ALLOWED_TOOLS || '').split(',').map(s => s.trim()).filter(Boolean);
const HAS_REPO  = process.env.AGENTHUB_HAS_REPO === '1';
const HAS_MCP   = process.env.AGENTHUB_HAS_MCP === '1';
const RESUME    = process.env.AGENTHUB_RESUME === '1';
const CSID      = process.env.AGENTHUB_CLAUDE_SESSION_ID || '';
const CALLBACK  = process.env.AGENTHUB_CALLBACK_URL || '';
const TOKEN     = process.env.AGENTHUB_CALLBACK_TOKEN || '';
const STATE_PUT = process.env.AGENTHUB_STATE_PUT_URL || '';
const SCROLL_PUT= process.env.AGENTHUB_SCROLLBACK_PUT_URL || '';
const HOME      = process.env.HOME || '/home/agent';
// Working dir: the backend sets AGENTHUB_WORKDIR (/workspace/repo for a single
// repo, /workspace when multiple repos are checked out side by side). Fall back to
// /workspace if the target does not exist (e.g. a repo that was not (re)cloned) so
// node-pty's chdir cannot fail.
const WORKDIR   = process.env.AGENTHUB_WORKDIR || (HAS_REPO ? '/workspace/repo' : '/workspace');
const CWD       = fs.existsSync(WORKDIR) ? WORKDIR : '/workspace';

// ---- Claude command ------------------------------------------------------------
function buildCommand(allowResume) {
  const args = [];
  if (HAS_MCP) args.push('--mcp-config', '/secrets/mcp/mcp.json');

  // Resume only when the entrypoint restored state AND we still want to try it.
  // If resume fails (no conversation for this id — e.g. an empty session), we
  // relaunch with allowResume=false, which uses a fresh --session-id instead.
  const restored = fs.existsSync('/tmp/.state-restored');
  if (allowResume && RESUME && CSID && restored) args.push('--resume', CSID);
  else if (CSID) args.push('--session-id', CSID);

  if (MODE !== 'interactive') {
    args.push('-p', PROMPT, '--permission-mode', 'acceptEdits');
    if (ALLOWED.length) args.push('--allowedTools', ALLOWED.join(','));
  }
  return { cmd: 'claude', args };
}

// ---- Scrollback --------------------------------------------------------------
const MAX_BUFFER = 1_000_000;
let scrollback = '';
function remember(c) { scrollback += c; if (scrollback.length > MAX_BUFFER) scrollback = scrollback.slice(-MAX_BUFFER); }

const clients = new Set();
let exited = false;
let term;                 // current Claude PTY (reassigned if we relaunch fresh)
let retriedFresh = false; // guard: only fall back from --resume once
let usedResume = false;
let sawNoConversation = false;
let launchedAt = 0;

function startClaude(allowResume) {
  const { cmd, args } = buildCommand(allowResume);
  usedResume = args.includes('--resume');
  sawNoConversation = false;
  launchedAt = Date.now();
  console.log(`[agent] mode=${MODE} resume=${usedResume} cwd=${CWD} cmd=${cmd} ${args.join(' ')}`);
  term = pty.spawn(cmd, args, { name: 'xterm-256color', cols: 120, rows: 32, cwd: CWD, env: process.env });

  term.onData(data => {
    if (usedResume && !retriedFresh && data.includes('No conversation found')) sawNoConversation = true;
    remember(data);
    for (const ws of clients) safeSend(ws, data);
  });

  term.onExit(({ exitCode, signal }) => {
    // Resume with nothing to resume → relaunch once as a fresh session.
    if (usedResume && !retriedFresh && exitCode !== 0 &&
        (sawNoConversation || Date.now() - launchedAt < 10000)) {
      retriedFresh = true;
      remember('\r\n[agent] No saved conversation to resume — starting fresh.\r\n');
      startClaude(false);
      return;
    }
    exited = true;
    const msg = `\r\n[agent] Session ended (code ${exitCode}${signal ? `, signal ${signal}` : ''}).\r\n`;
    remember(msg);
    for (const ws of clients) { safeSend(ws, msg); try { ws.close(1000); } catch {} }
    // Save the final state, report the status, then exit.
    persistAll(() => {
      postStatus(exitCode === 0 ? 'Succeeded' : 'Failed', () =>
        setTimeout(() => process.exit(exitCode || 0), MODE === 'interactive' ? 1500 : 200));
    });
  });
}

startClaude(true);

function safeSend(ws, data) { if (ws.readyState === ws.OPEN) { try { ws.send(data); } catch {} } }

// ---- Persistence to S3 ---------------------------------------------------------
// -k for presigned uploads to an internal S3/MinIO with a self-signed cert (opt-in).
const CURL_K = process.env.AGENTHUB_S3_INSECURE === '1' ? '-k ' : '';
function persistState(done) {
  if (!STATE_PUT) return done && done();
  // Pack up ~/.claude and upload it via presigned PUT.
  execFile('/bin/sh', ['-c',
    `tar czf /tmp/state.tgz -C "${HOME}" .claude 2>/dev/null && curl -fsS ${CURL_K}-T /tmp/state.tgz "${STATE_PUT}"`
  ], () => done && done());
}
function persistScrollback(done) {
  if (!SCROLL_PUT) return done && done();
  try { fs.writeFileSync('/tmp/scrollback.log', scrollback); } catch {}
  execFile('/bin/sh', ['-c', `curl -fsS ${CURL_K}-T /tmp/scrollback.log "${SCROLL_PUT}"`], () => done && done());
}
// Also store the transcript via the backend callback, so it is available even
// on instances without S3 (autonomous runs would otherwise leave nothing behind).
function backupScrollback(done) {
  if (!CALLBACK) return done && done();
  fetch(`${CALLBACK}/scrollback`, {
    method: 'PUT',
    headers: { 'Content-Type': 'text/plain', 'X-Agent-Token': TOKEN },
    body: scrollback
  }).catch(() => {}).finally(() => done && done());
}
function persistAll(done) { backupScrollback(() => persistScrollback(() => persistState(done))); }

function postStatus(status, done) {
  if (!CALLBACK) return done && done();
  fetch(`${CALLBACK}/status`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Agent-Token': TOKEN },
    body: JSON.stringify({ status })
  }).catch(() => {}).finally(() => done && done());
}

// Persist periodically (also during long autonomous runs).
setInterval(() => { if (!exited) persistAll(); }, 30_000);

// ---- WebSocket ---------------------------------------------------------------
const wss = new WebSocketServer({ port: PORT, handleProtocols: () => 'tty' });
wss.on('connection', (ws, req) => {
  const path = (req && req.url ? req.url : '/').split('?')[0];
  if (path === '/shell') handleShell(ws);
  else handleAgent(ws);
});

// Shared Claude terminal: all clients see the same PTY and its scrollback.
function handleAgent(ws) {
  clients.add(ws);
  if (scrollback) safeSend(ws, scrollback);
  ws.on('message', raw => {
    let m; try { m = JSON.parse(raw.toString()); } catch { return; }
    if (m.type === 'input' && typeof m.data === 'string' && !exited) term.write(m.data);
    else if (m.type === 'resize' && m.cols > 0 && m.rows > 0 && !exited) { try { term.resize(m.cols, m.rows); } catch {} }
  });
  ws.on('close', () => clients.delete(ws));
  ws.on('error', () => clients.delete(ws));
}

// Interactive shell: a fresh `bash -l` PTY per connection, spawned on demand in
// the working directory. Independent of the Claude terminal (own process, no
// shared scrollback); killed when the client disconnects.
function handleShell(ws) {
  let sh;
  try {
    sh = pty.spawn('bash', ['-l'], { name: 'xterm-256color', cols: 120, rows: 32, cwd: CWD, env: process.env });
  } catch (e) {
    safeSend(ws, `\r\n[agent] Failed to start shell: ${e && e.message}\r\n`);
    try { ws.close(1011); } catch {}
    return;
  }
  sh.onData(d => safeSend(ws, d));
  sh.onExit(() => { try { ws.close(1000); } catch {} });
  ws.on('message', raw => {
    let m; try { m = JSON.parse(raw.toString()); } catch { return; }
    if (m.type === 'input' && typeof m.data === 'string') sh.write(m.data);
    else if (m.type === 'resize' && m.cols > 0 && m.rows > 0) { try { sh.resize(m.cols, m.rows); } catch {} }
  });
  const cleanup = () => { try { sh.kill(); } catch {} };
  ws.on('close', cleanup);
  ws.on('error', cleanup);
}

console.log(`[agent] WebSocket terminal listening on :${PORT} (paths: / and /shell)`);
postStatus('Running');

for (const sig of ['SIGTERM', 'SIGINT'])
  process.on(sig, () => { persistAll(() => { try { term.kill(); } catch {} ; process.exit(0); }); });
