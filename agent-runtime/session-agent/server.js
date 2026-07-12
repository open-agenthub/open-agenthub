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
// repo, /workspace when multiple repos are checked out side by side).
const CWD       = process.env.AGENTHUB_WORKDIR || (HAS_REPO ? '/workspace/repo' : '/workspace');

// ---- Claude command ------------------------------------------------------------
function buildCommand() {
  const args = [];
  if (HAS_MCP) args.push('--mcp-config', '/secrets/mcp/mcp.json');

  if (RESUME && CSID) {
    args.push('--resume', CSID);
  } else if (CSID) {
    args.push('--session-id', CSID); // fixed ID so the session can be resumed later
  }

  if (MODE === 'interactive') return { cmd: 'claude', args };

  // autonomous / scheduled: headless, permissions limited via allowlist
  args.push('-p', PROMPT, '--permission-mode', 'acceptEdits');
  if (ALLOWED.length) args.push('--allowedTools', ALLOWED.join(','));
  return { cmd: 'claude', args };
}

const { cmd, args } = buildCommand();
console.log(`[agent] mode=${MODE} resume=${RESUME} cwd=${CWD} cmd=${cmd} ${args.join(' ')}`);

const term = pty.spawn(cmd, args, {
  name: 'xterm-256color', cols: 120, rows: 32, cwd: CWD, env: process.env
});

// ---- Scrollback --------------------------------------------------------------
const MAX_BUFFER = 1_000_000;
let scrollback = '';
function remember(c) { scrollback += c; if (scrollback.length > MAX_BUFFER) scrollback = scrollback.slice(-MAX_BUFFER); }

const clients = new Set();
let exited = false;

term.onData(data => { remember(data); for (const ws of clients) safeSend(ws, data); });

term.onExit(({ exitCode, signal }) => {
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

function safeSend(ws, data) { if (ws.readyState === ws.OPEN) { try { ws.send(data); } catch {} } }

// ---- Persistence to S3 ---------------------------------------------------------
function persistState(done) {
  if (!STATE_PUT) return done && done();
  // Pack up ~/.claude and upload it via presigned PUT.
  execFile('/bin/sh', ['-c',
    `tar czf /tmp/state.tgz -C "${HOME}" .claude 2>/dev/null && curl -fsS -T /tmp/state.tgz "${STATE_PUT}"`
  ], () => done && done());
}
function persistScrollback(done) {
  if (!SCROLL_PUT) return done && done();
  try { fs.writeFileSync('/tmp/scrollback.log', scrollback); } catch {}
  execFile('/bin/sh', ['-c', `curl -fsS -T /tmp/scrollback.log "${SCROLL_PUT}"`], () => done && done());
}
function persistAll(done) { persistScrollback(() => persistState(done)); }

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
wss.on('connection', ws => {
  clients.add(ws);
  if (scrollback) safeSend(ws, scrollback);
  ws.on('message', raw => {
    let m; try { m = JSON.parse(raw.toString()); } catch { return; }
    if (m.type === 'input' && typeof m.data === 'string' && !exited) term.write(m.data);
    else if (m.type === 'resize' && m.cols > 0 && m.rows > 0 && !exited) { try { term.resize(m.cols, m.rows); } catch {} }
  });
  ws.on('close', () => clients.delete(ws));
  ws.on('error', () => clients.delete(ws));
});

console.log(`[agent] WebSocket terminal listening on :${PORT}`);
postStatus('Running');

for (const sig of ['SIGTERM', 'SIGINT'])
  process.on(sig, () => { persistAll(() => { try { term.kill(); } catch {} ; process.exit(0); }); });
