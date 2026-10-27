// #336 rig: does the context_clear handover reach the fresh conversation?
// REAL pinned CLI (AGENT=claude|codex) in the REAL common/server.js PTY under proot (the built
// tree is /opt/session-agent, /etc/codex is the shipped requirements.toml, /tmp is private),
// with the production hooks. Fixtures: a mock model that records every request, a stand-in hub
// (policy allow, empty inbox), and the clear request POSTed to the pod's /agenthub/context-clear
// the way the backend forwards the context_clear tool call. fault-server.cjs injects the fault.
//
//   R=<rig dir> AGENT=claude|codex FAULT=none|drop|busy|restart node rig.cjs
// Prints one `RESULT {...}` line.
'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn, execFileSync } = require('node:child_process');
const { createRequire } = require('node:module');

const R = process.env.R;
const S = path.dirname(R);
const rtRequire = createRequire(R + '/rt/package.json');
const { Terminal } = rtRequire('@xterm/headless');
const WebSocket = rtRequire('ws');
const AGENT = process.env.AGENT || 'claude';
const CODEX = AGENT === 'codex';
const FAULT = process.env.FAULT || 'none';
const WAIT_MS = Number(process.env.WAIT_MS || 90_000);
const HUB = 19300 + Math.floor(Math.random() * 300);
const PORT = HUB + 1000;
const TOKEN = 'rig336-token';
const run = fs.mkdtempSync(R + '/run-' + AGENT + '-' + FAULT + '-');
const home = run + '/home', work = run + '/work';
fs.mkdirSync(home + '/.codex', { recursive: true });
fs.mkdirSync(home + '/.claude', { recursive: true });
fs.mkdirSync(work);
execFileSync('git', ['init', '-q', work]);
const t0 = Date.now();
const logFile = fs.createWriteStream(run + '/rig.log');
const say = (...a) => { const line = `[${((Date.now() - t0) / 1000).toFixed(1)}s] ` + a.join(' '); console.log(line); logFile.write(line + '\n'); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, ms) { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return true; await sleep(150); } return false; }

const OLD = 'OLD-MARKER-336';
const HANDOVER = 'HANDOVER-336 State: the patch is written. Open task: run the tests and push.';
const requests = []; // { at, text, helper }
let clearPostedAt = 0;
let oldSeen = false;

// ---- mock model + stand-in hub ------------------------------------------------------------
const sse = events => events.map(e => `event: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`).join('');
const hub = http.createServer(async (req, res) => {
  let raw = '';
  for await (const chunk of req) raw += chunk;
  let body = {};
  try { body = raw ? JSON.parse(raw) : {}; } catch {}
  const url = new URL(req.url, 'http://x');
  const json = value => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(value)); };
  if (url.pathname.startsWith('/v1/messages')) {
    if (url.pathname.includes('count_tokens') || !body.stream) return json({ input_tokens: 10, id: 'x', type: 'message', role: 'assistant', model: 'm', content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } });
    const text = JSON.stringify(body.messages || []);
    const helper = !(body.tools && body.tools.length) || /Reply with ONLY the suggestion/.test(text);
    requests.push({ at: Date.now(), text, helper });
    fs.writeFileSync(run + '/model-requests.json', JSON.stringify(requests, null, 1));
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    const ev = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
    ev('message_start', { message: { id: 'm' + requests.length, type: 'message', role: 'assistant', model: body.model || 'm', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } } });
    const answer = await modelAnswer(text, helper);
    ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
    ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: answer } });
    ev('content_block_stop', { index: 0 });
    ev('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } });
    ev('message_stop', {});
    return res.end();
  }
  if (url.pathname === '/v1/responses') {
    const text = JSON.stringify(body.input || []);
    const helper = /Generate a concise, single-line task title/.test(text) || !body.stream;
    requests.push({ at: Date.now(), text, helper });
    fs.writeFileSync(run + '/model-requests.json', JSON.stringify(requests, null, 1));
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    res.write(sse([{ type: 'response.created', response: { id: 'r' + requests.length } }]));
    const answer = await modelAnswer(text, helper);
    return res.end(sse([
      { type: 'response.output_item.done', item: { type: 'message', role: 'assistant', id: 'o' + requests.length, content: [{ type: 'output_text', text: answer }] } },
      { type: 'response.completed', response: { id: 'r' + requests.length, usage: { input_tokens: 0, input_tokens_details: null, output_tokens: 0, output_tokens_details: null, total_tokens: 0 } } }
    ]));
  }
  if (url.pathname.endsWith('/messages')) return json({ messages: [] });
  if (url.pathname.includes('/permission')) return json({ decision: 'allow', id: 'p336' });
  if (url.pathname.endsWith('/scrollback') || url.pathname.endsWith('/transcript')) { res.statusCode = 404; return res.end(); }
  return json({ allowed: true, decision: 'allow' });
});

async function modelAnswer(text, helper) {
  if (helper) return 'Rig title';
  if (text.includes('HANDOVER-336')) return 'HANDOVER-ACK';
  if (text.includes(OLD) && !oldSeen) {
    oldSeen = true;
    // The context_clear call happens inside the turn; the backend forwards it to the pod.
    const response = await fetch(`http://127.0.0.1:${PORT}/agenthub/context-clear`, {
      method: 'POST', headers: { 'X-Agent-Token': TOKEN, 'Content-Type': 'application/json' },
      body: JSON.stringify({ followUp: HANDOVER })
    });
    clearPostedAt = Date.now();
    say('context-clear scheduled: ' + response.status + ' ' + await response.text());
    await sleep(3000);
    return 'OLD-ANSWER';
  }
  return 'OK';
}

// ---- session agent under proot ------------------------------------------------------------
fs.writeFileSync(home + '/.codex/config.toml', [
  'cli_auth_credentials_store = "file"', 'model = "gpt-5.4"', 'model_provider = "rig"',
  '[model_providers.rig]', 'name = "rig"', `base_url = "http://127.0.0.1:${HUB}/v1"`,
  'wire_api = "responses"', 'env_key = "CODEX_API_KEY"', 'supports_websockets = false', 'request_max_retries = 0',
  `[projects."${work}"]`, 'trust_level = "trusted"', ''
].join('\n'));
const claudeKey = 'sk-ant-rig-0000000000000000000000000336';
fs.writeFileSync(home + '/.claude.json', JSON.stringify({ hasCompletedOnboarding: true, autoUpdates: false,
  customApiKeyResponses: { approved: [claudeKey.slice(-20)], rejected: [] }, projects: { [work]: { hasTrustDialogAccepted: true } } }));
const claudeSessionId = crypto.randomUUID();
const baseEnv = {
  PATH: `${S}/clis/node_modules/.bin:/usr/local/bin:/usr/bin:/bin`, HOME: home, TMPDIR: '/tmp', TERM: 'xterm-256color', LANG: 'C.UTF-8',
  AGENTHUB_MODE: 'interactive', AGENTHUB_PORT: String(PORT), AGENTHUB_SESSION_ID: 's336',
  AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${HUB}/internal/sessions/s336`, AGENTHUB_CALLBACK_TOKEN: TOKEN,
  AGENTHUB_WORKDIR: work, AGENTHUB_SKILLS_SYNC_SECONDS: '0'
};
if (CODEX) Object.assign(baseEnv, { CODEX_HOME: home + '/.codex', CODEX_API_KEY: 'rig-key', AGENTHUB_DRIVER: '/opt/session-agent/codex/driver.js', PROOF_CLEAR_COMMAND: '/new' });
else {
  Object.assign(baseEnv, { AGENTHUB_DRIVER: '/opt/session-agent/claude/driver.js', ANTHROPIC_API_KEY: claudeKey, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + HUB,
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1', DISABLE_AUTOUPDATER: '1', AGENTHUB_CLAUDE_SESSION_ID: claudeSessionId, PROOF_CLEAR_COMMAND: '/clear' });
  // The hooks the entrypoint installs: without them there are no turn events.
  const settings = JSON.parse(execFileSync('bash', [R + '/rt/claude/hooks/mcp-policy-hook.sh', '--settings'],
    { env: { ...baseEnv, RUNTIME: '/opt/session-agent', AGENTHUB_RUNTIME: '/opt/session-agent/claude/hooks' }, encoding: 'utf8' }));
  fs.writeFileSync(home + '/.claude/settings.json', JSON.stringify(settings));
}

let proc = null;
function startAgent(extraEnv, label) {
  const env = { ...baseEnv, ...extraEnv };
  const tmp = fs.mkdtempSync(run + '/tmp-');
  proc = spawn(S + '/proot', ['-b', R + '/rt:/opt/session-agent', '-b', R + '/etc-codex:/etc/codex', '-b', tmp + ':/tmp',
    'node', '/opt/session-agent/fault-server.cjs'], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  const out = fs.createWriteStream(run + '/agent-' + label + '.log');
  proc.stdout.on('data', d => out.write(d));
  proc.stderr.on('data', d => out.write(d));
  return proc;
}

const term = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
let ws = null;
const screen = () => { const b = term.buffer.active; const rows = []; for (let i = 0; i < term.rows; i++) rows.push(b.getLine(b.viewportY + i)?.translateToString(true) ?? ''); return rows.join('\n'); };
async function connect() {
  for (let i = 0; i < 100; i++) {
    try {
      ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
      await new Promise((resolve, reject) => { ws.once('open', resolve); ws.once('error', reject); });
      break;
    } catch { await sleep(200); }
  }
  ws.on('message', d => term.write(d.toString()));
  term.onData(data => { if (ws.readyState === ws.OPEN) ws.send(JSON.stringify({ type: 'input', data })); });
}
const ready = () => CODEX ? /›/.test(screen()) && /Codex/.test(screen()) : /for shortcuts|❯/.test(screen());

// The first user turn of the newest native transcript, as the CLI wrote it.
function nativeFirstUserTurn() {
  const files = [];
  const walk = dir => { try { for (const e of fs.readdirSync(dir, { withFileTypes: true })) { const p = path.join(dir, e.name); if (e.isDirectory()) walk(p); else if (p.endsWith('.jsonl')) files.push(p); } } catch {} };
  walk(CODEX ? home + '/.codex/sessions' : home + '/.claude/projects');
  const withHandover = files.filter(f => fs.readFileSync(f, 'utf8').includes('HANDOVER-336'));
  const result = { transcripts: files.length, withHandover: withHandover.length };
  for (const file of withHandover) {
    const users = [];
    for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
      let record; try { record = JSON.parse(line); } catch { continue; }
      if (CODEX) {
        const p = record.payload;
        if (record.type === 'response_item' && p && p.type === 'message' && p.role === 'user') {
          const t = (p.content || []).map(c => c.text || '').join('');
          if (!/^<environment_context>|^# AGENTS\.md|^<user_instructions>/.test(t)) users.push(t);
        }
      } else if (record.type === 'user' && record.message && !record.isMeta) {
        const c = record.message.content;
        const t = typeof c === 'string' ? c : (c || []).map(x => x.text || '').join('');
        if (!/^<command-name>|^<local-command-stdout>|^<command-message>/.test(t)) users.push(t);
      }
    }
    result.file = path.basename(file);
    result.firstUserTurn = (users[0] || '').slice(0, 80);
    result.oldMarkerInFile = fs.readFileSync(file, 'utf8').includes(OLD);
    result.userTurns = users.length;
  }
  return result;
}

(async () => {
  await new Promise(r => hub.listen(HUB, '127.0.0.1', r));
  startAgent({ PROOF_FAULT: FAULT }, 'first');
  await connect();
  if (!await until(ready, 60_000)) { say('CLI not ready'); fs.writeFileSync(run + '/screen-notready.txt', screen()); process.exit(2); }
  await sleep(3000);
  ws.send(JSON.stringify({ type: 'input', data: '\x1b[200~' + OLD + ': start the task.\x1b[201~' }));
  await sleep(400);
  ws.send(JSON.stringify({ type: 'input', data: '\r' }));
  if (!await until(() => clearPostedAt > 0, 60_000)) { say('old turn never reached the model'); fs.writeFileSync(run + '/screen-old.txt', screen()); process.exit(2); }
  if (FAULT === 'restart') {
    await new Promise(resolve => proc.once('exit', resolve));
    say('pod lost; starting a new one on the same home (resume)');
    try { ws.terminate(); } catch {}
    term.reset();
    await sleep(500);
    startAgent({ PROOF_FAULT: 'none', AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1' }, 'second');
    await connect();
  }
  const delivered = await until(() => requests.some(r => !r.helper && r.text.includes('HANDOVER-336')), WAIT_MS);
  await sleep(4000);
  fs.writeFileSync(run + '/screen-end.txt', screen());
  const first = requests.find(r => !r.helper && r.text.includes('HANDOVER-336'));
  const logs = fs.readdirSync(run).filter(f => f.startsWith('agent-')).map(f => fs.readFileSync(run + '/' + f, 'utf8')).join('\n');
  const result = {
    agent: AGENT, fault: FAULT, run,
    handoverDelivered: delivered,
    secondsAfterSchedule: first ? +((first.at - clearPostedAt) / 1000).toFixed(1) : null,
    freshContext: first ? !first.text.includes(OLD) : null,
    handoverRequests: requests.filter(r => !r.helper && r.text.includes('HANDOVER-336') && r.text.includes('[AgentHub context handover]')).length,
    native: nativeFirstUserTurn(),
    agentLog: logs.split('\n').filter(l => /context|handover|Context|\[fault\]|Conversation/.test(l)).slice(0, 20)
  };
  say('RESULT ' + JSON.stringify(result));
  try { process.kill(proc.pid, 'SIGTERM'); } catch {}
  await sleep(500);
  process.exit(0);
})().catch(error => { say('ERROR ' + (error && error.stack || error)); process.exit(1); });
