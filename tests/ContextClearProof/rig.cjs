// #336 rig: does the context_clear handover reach the fresh conversation?
// REAL pinned CLI (AGENT=claude|codex) in the REAL common/server.js PTY under proot (the built
// tree is /opt/session-agent, /etc/codex is the shipped requirements.toml, /tmp is private),
// with the production hooks. Fixtures: a mock model that records every request, a stand-in hub
// (policy allow, empty inbox), and the clear request POSTed to the pod's /agenthub/context-clear
// the way the backend forwards the context_clear tool call. fault-server.cjs injects the fault.
//
//   R=<rig dir> AGENT=claude|codex FAULT=none|drop|busy|restart node rig.cjs
// Options, from the owner's report: QUEUED=1 has the owner send a message while the clearing
// turn still runs (the CLI queues it); REMINDER=1 makes that turn count as work, so the
// production skill-reminder Stop hook continues it once before it ends. MENU_ANSWER_MS=<ms>
// answers an open CLI menu with Enter that long after the clear, then the owner sends a prompt.
// FOLLOW=1 (with FAULT=none) then checks that the Workspace follows the new conversation: the
// pod's live transcript and the backend's conversation page (HOST_DLL, Host/ built with
// `dotnet build -o`) show "Conversation cleared" with the new thread's identity, and a later turn
// lands after it. RESTART=1 adds a pod restart after that: the new pod resumes the new thread.
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
let currentTmp = null;
const handoverFrames = [];
const QUEUED = process.env.QUEUED === '1';
const REMINDER = process.env.REMINDER === '1';
const FOLLOW = process.env.FOLLOW === '1' || process.env.RESTART === '1';
const RESTART = process.env.RESTART === '1';
const HOST_PORT = HUB + 2000;
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
  if (text.includes('Before you finish: is there a reusable procedure') && !text.includes('REMINDER-ANSWERED')) return 'REMINDER-ANSWERED nothing to save';
  if (text.includes(OLD) && !oldSeen) {
    oldSeen = true;
    // The context_clear call happens inside the turn; the backend forwards it to the pod.
    const response = await fetch(`http://127.0.0.1:${PORT}/agenthub/context-clear`, {
      method: 'POST', headers: { 'X-Agent-Token': TOKEN, 'Content-Type': 'application/json' },
      body: JSON.stringify({ followUp: HANDOVER })
    });
    clearPostedAt = Date.now();
    // Only the status is logged: the rig records assertions, not what came over the network.
    say('context-clear scheduled: HTTP ' + Number(response.status));
    if (REMINDER) {
      // What the PostToolUse --mark hook writes after a changing tool call in this turn.
      const id = CODEX ? rolloutId() : claudeSessionId;
      const key = crypto.createHash('sha256').update(String(id)).digest('hex').slice(0, 16);
      fs.writeFileSync(currentTmp + '/.agenthub-skill-work-' + key, '');
      say('skill reminder armed for ' + id);
    }
    if (QUEUED) {
      ws.send(JSON.stringify({ type: 'input', data: '\x1b[200~QUEUED-OWNER-336 a message sent while the agent worked\x1b[201~' }));
      await sleep(400);
      ws.send(JSON.stringify({ type: 'input', data: '\r' }));
      say('owner message sent mid-turn');
    }
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
// The fault proxy fires on the clear command the built driver types; taken from that driver so
// main and a branch that changed it (Codex /new -> /clear) are both faulted.
const clearCommandOf = name => (/contextClearCommand:\s*'([^']+)'/.exec(fs.readFileSync(`${R}/rt/${name}/driver.js`, 'utf8')) || [])[1];
const baseEnv = {
  PATH: `${S}/clis/node_modules/.bin:/usr/local/bin:/usr/bin:/bin`, HOME: home, TMPDIR: '/tmp', TERM: 'xterm-256color', LANG: 'C.UTF-8',
  AGENTHUB_MODE: 'interactive', AGENTHUB_PORT: String(PORT), AGENTHUB_SESSION_ID: 's336',
  AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${HUB}/internal/sessions/s336`, AGENTHUB_CALLBACK_TOKEN: TOKEN,
  AGENTHUB_WORKDIR: work, AGENTHUB_SKILLS_SYNC_SECONDS: '0'
};
if (CODEX) Object.assign(baseEnv, { CODEX_HOME: home + '/.codex', CODEX_API_KEY: 'rig-key', AGENTHUB_DRIVER: '/opt/session-agent/codex/driver.js', PROOF_CLEAR_COMMAND: clearCommandOf('codex') });
else {
  Object.assign(baseEnv, { AGENTHUB_DRIVER: '/opt/session-agent/claude/driver.js', ANTHROPIC_API_KEY: claudeKey, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + HUB,
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1', DISABLE_AUTOUPDATER: '1', AGENTHUB_CLAUDE_SESSION_ID: claudeSessionId, PROOF_CLEAR_COMMAND: clearCommandOf('claude') });
  // The hooks the entrypoint installs: without them there are no turn events.
  const settings = JSON.parse(execFileSync('bash', [R + '/rt/claude/hooks/mcp-policy-hook.sh', '--settings'],
    { env: { ...baseEnv, RUNTIME: '/opt/session-agent', AGENTHUB_RUNTIME: '/opt/session-agent/claude/hooks' }, encoding: 'utf8' }));
  fs.writeFileSync(home + '/.claude/settings.json', JSON.stringify(settings));
}

let proc = null;
function startAgent(extraEnv, label) {
  const env = { ...baseEnv, ...extraEnv };
  const tmp = fs.mkdtempSync(run + '/tmp-');
  currentTmp = tmp;
  proc = spawn(S + '/proot', ['-b', R + '/rt:/opt/session-agent', '-b', R + '/etc-codex:/etc/codex', '-b', tmp + ':/tmp',
    'node', '/opt/session-agent/fault-server.cjs'], { env, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
  const out = fs.createWriteStream(run + '/agent-' + label + '.log');
  proc.stdout.on('data', d => out.write(d));
  proc.stderr.on('data', d => out.write(d));
  return proc;
}

const term = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
let ws = null;
let terminalCleared = 0;
const screen = () => { const b = term.buffer.active; const rows = []; for (let i = 0; i < term.rows; i++) rows.push(b.getLine(b.viewportY + i)?.translateToString(true) ?? ''); return rows.join('\n'); };
async function connect() {
  for (let i = 0; i < 100; i++) {
    try {
      ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
      await new Promise((resolve, reject) => { ws.once('open', resolve); ws.once('error', reject); });
      break;
    } catch { await sleep(200); }
  }
  ws.send(JSON.stringify({ type: 'subscribe', channel: 'activity' }));
  ws.on('message', d => {
    const data = d.toString();
    if (data.startsWith('{"type":"agenthub","subtype":"handover"')) { const frame = JSON.parse(data);
      handoverFrames.push({ failed: frame.failed === true, ...(/^context_clear_[a-z_]+$/.test(frame.reason) ? { reason: frame.reason } : {}) }); say('handover frame failed=' + (frame.failed === true)); return; }
    if (data.startsWith('{"type":"agenthub"')) return;
    if (data.includes('[agent] Conversation cleared.')) terminalCleared++;
    term.write(data);
  });
  term.onData(data => { if (ws.readyState === ws.OPEN) ws.send(JSON.stringify({ type: 'input', data })); });
}
const ready = () => CODEX ? /›/.test(screen()) && /Codex/.test(screen()) : /for shortcuts|❯/.test(screen());

function rolloutId() {
  const files = [];
  const walk = dir => { try { for (const e of fs.readdirSync(dir, { withFileTypes: true })) { const p = path.join(dir, e.name); if (e.isDirectory()) walk(p); else files.push(p); } } catch {} };
  walk(home + '/.codex/sessions');
  const match = files.map(f => /-([0-9a-f-]{36})\.jsonl$/.exec(f)).filter(Boolean).pop();
  return match ? match[1] : '';
}

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

// ---- Workspace follow (FOLLOW=1 / RESTART=1) ----------------------------------------------
const rolloutIdOf = name => (/-([0-9a-f-]{36})\.jsonl$/.exec(name || '') || [])[1] || '';
async function live() {
  const response = await fetch(`http://127.0.0.1:${PORT}/agenthub/transcript?from=0`, { headers: { 'X-Agent-Token': TOKEN } });
  return { id: response.headers.get('X-Transcript-Id'), text: await response.text() };
}
async function backendConversation() {
  if (!process.env.HOST_DLL) return null;
  try { return await (await fetch(`http://127.0.0.1:${HOST_PORT}/conversation?max=5000`)).json(); } catch { return null; }
}
// The page as the Workspace renders it: one tag per entry, so the order is the assertion.
function conversationTags(page) {
  return (page && page.entries || []).map(entry => {
    if (entry.role === 'boundary') return 'boundary:' + entry.text;
    const tag = ['OLD-MARKER-336', 'HANDOVER-336', 'LATER-351', 'AFTER-RESTART-351'].find(m => (entry.text || '').includes(m));
    return tag ? entry.role + ':' + tag : null;
  }).filter(Boolean);
}
function liveSummary(text) {
  const lines = text.split('\n').filter(Boolean);
  const boundary = lines.map(l => { try { return JSON.parse(l); } catch { return null; } }).find(r => r && r.type === 'agenthub_conversation');
  const at = marker => text.indexOf(marker);
  return {
    boundaryText: boundary ? boundary.text : null, boundaryThread: boundary ? rolloutIdOf(boundary.id) : null,
    oldBeforeBoundary: at(OLD) >= 0 && at(OLD) < at('agenthub_conversation'),
    handoverAfterBoundary: at('agenthub_conversation') >= 0 && at('HANDOVER-336') > at('agenthub_conversation')
  };
}
function codexResumeArgs() {
  const found = [];
  for (const pid of fs.readdirSync('/proc').filter(n => /^\d+$/.test(n))) {
    let args; try { args = fs.readFileSync(`/proc/${pid}/cmdline`, 'utf8').split('\0'); } catch { continue; }
    // Only this run's pod: the CLI's environment carries the run's home.
    try { if (!fs.readFileSync(`/proc/${pid}/environ`, 'utf8').split('\0').includes('HOME=' + home)) continue; } catch { continue; }
    const at = args.indexOf('resume');
    if (at > 0 && args.some(a => /codex/.test(a)) && args[at + 1]) found.push(args[at + 1]);
  }
  return [...new Set(found)];
}
async function paste(text) {
  ws.send(JSON.stringify({ type: 'input', data: '\x1b[200~' + text + '\x1b[201~' }));
  await sleep(400);
  ws.send(JSON.stringify({ type: 'input', data: '\r' }));
}
async function follow() {
  const out = {};
  const oldThread = rolloutIdOf(fs.readdirSync(home + '/.codex/sessions', { recursive: true }).map(String)
    .find(f => f.endsWith('.jsonl') && fs.readFileSync(home + '/.codex/sessions/' + f, 'utf8').includes(OLD)));
  const newThread = () => fs.readFileSync(home + '/.codex/agenthub-thread-id', 'utf8').trim();
  out.oldThread = oldThread;
  out.newThread = newThread();
  await until(async () => liveSummary((await live()).text).handoverAfterBoundary, 30_000);
  const first = await live();
  out.live = { id: first.id, ...liveSummary(first.text) };
  out.terminalCleared = terminalCleared;
  await until(async () => conversationTags(await backendConversation()).includes('user:HANDOVER-336'), 30_000);
  out.backendAfterClear = conversationTags(await backendConversation());
  await paste('LATER-351 and the next step?');
  await until(() => requests.some(r => !r.helper && r.text.includes('LATER-351')), 60_000);
  const later = requests.find(r => !r.helper && r.text.includes('LATER-351'));
  out.laterRequest = later ? { handover: later.text.includes('HANDOVER-336'), old: later.text.includes(OLD) } : null;
  await until(async () => conversationTags(await backendConversation()).includes('user:LATER-351'), 30_000);
  out.backendAfterLater = conversationTags(await backendConversation());
  if (!RESTART) return out;

  say('restart: killing the pod after the handover and a later turn');
  await sleep(2000);
  try { process.kill(-proc.pid, 'SIGKILL'); } catch {}
  await new Promise(resolve => proc.exitCode !== null || proc.signalCode ? resolve() : proc.once('exit', resolve));
  try { ws.terminate(); } catch {}
  term.reset();
  // Excluded from the state archive (driver stateExcludes), so a new pod never has it.
  fs.rmSync(home + '/.codex/agenthub-active', { recursive: true, force: true });
  await sleep(500);
  startAgent({ PROOF_FAULT: 'none', AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1' }, 'resumed');
  await connect();
  if (!await until(ready, 60_000)) throw new Error('resumed CLI not ready');
  out.resumeArgs = codexResumeArgs();
  await sleep(3000);
  await paste('AFTER-RESTART-351 still there?');
  await until(() => requests.some(r => !r.helper && r.text.includes('AFTER-RESTART-351')), 60_000);
  const resumed = requests.find(r => !r.helper && r.text.includes('AFTER-RESTART-351'));
  out.resumedRequest = resumed ? { handover: resumed.text.includes('HANDOVER-336'), later: resumed.text.includes('LATER-351'), old: resumed.text.includes(OLD) } : null;
  const rollouts = fs.readdirSync(home + '/.codex/sessions', { recursive: true }).map(String).filter(f => f.endsWith('.jsonl'));
  out.afterRestartIn = rollouts.filter(f => fs.readFileSync(home + '/.codex/sessions/' + f, 'utf8').includes('AFTER-RESTART-351')).map(rolloutIdOf);
  await until(async () => conversationTags(await backendConversation()).includes('user:AFTER-RESTART-351'), 30_000);
  const page = await backendConversation();
  out.backendAfterRestart = conversationTags(page);
  out.liveIdAfterRestart = (await live()).id;
  return out;
}

(async () => {
  await new Promise(r => hub.listen(HUB, '127.0.0.1', r));
  let host = null;
  if (process.env.HOST_DLL) {
    host = spawn('dotnet', [process.env.HOST_DLL], { env: { ...process.env, PROOF_AGENT_PORT: String(PORT), PROOF_HOST_PORT: String(HOST_PORT) }, stdio: ['ignore', 'pipe', 'pipe'] });
    const out = fs.createWriteStream(run + '/host.log');
    host.stdout.on('data', d => out.write(d)); host.stderr.on('data', d => out.write(d));
    process.on('exit', () => { try { host.kill('SIGKILL'); } catch {} });
  }
  startAgent({ PROOF_FAULT: FAULT }, 'first');
  await connect();
  if (!await until(ready, 60_000)) { say('CLI not ready'); console.log(screen()); process.exit(2); }
  await sleep(3000);
  ws.send(JSON.stringify({ type: 'input', data: '\x1b[200~' + OLD + ': start the task.\x1b[201~' }));
  await sleep(400);
  ws.send(JSON.stringify({ type: 'input', data: '\r' }));
  if (!await until(() => clearPostedAt > 0, 60_000)) { say('old turn never reached the model'); console.log(screen()); process.exit(2); }
  if (FAULT === 'restart') {
    await new Promise(resolve => proc.once('exit', resolve));
    say('pod lost; starting a new one on the same home (resume)');
    try { ws.terminate(); } catch {}
    term.reset();
    await sleep(500);
    startAgent({ PROOF_FAULT: 'none', AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1' }, 'second');
    await connect();
  }
  if (process.env.MENU_ANSWER_MS) {
    await sleep(Number(process.env.MENU_ANSWER_MS));
    console.log(screen());
    say('handover typed into the menu? ' + requests.some(r => r.text.includes('HANDOVER-336')));
    ws.send(JSON.stringify({ type: 'input', data: '\r' }));
    say('menu answered with Enter');
    await sleep(3000);
    ws.send(JSON.stringify({ type: 'input', data: '\x1b[200~OWNER-NEXT-336 what now?\x1b[201~' }));
    await sleep(400);
    ws.send(JSON.stringify({ type: 'input', data: '\r' }));
  }
  const delivered = await until(() => requests.some(r => !r.helper && r.text.includes('HANDOVER-336')), WAIT_MS);
  await sleep(4000);
  const followed = FOLLOW && delivered ? await follow() : undefined;
  if (process.env.SHOW_SCREEN) console.log(screen());
  const first = requests.find(r => !r.helper && r.text.includes('HANDOVER-336'));
  const logs = fs.readdirSync(run).filter(f => f.startsWith('agent-')).map(f => fs.readFileSync(run + '/' + f, 'utf8')).join('\n');
  const result = {
    agent: AGENT, fault: FAULT, run,
    handoverDelivered: delivered,
    secondsAfterSchedule: first ? +((first.at - clearPostedAt) / 1000).toFixed(1) : null,
    freshContext: first ? !first.text.includes(OLD) : null,
    handoverRequests: requests.filter(r => !r.helper && r.text.includes('HANDOVER-336') && r.text.includes('[AgentHub context handover]')).length,
    native: nativeFirstUserTurn(),
    order: requests.filter(r => !r.helper).map(r => {
      const tags = [];
      if (r.text.includes('HANDOVER-336')) tags.push('handover');
      if (r.text.includes('QUEUED-OWNER-336')) tags.push('queued');
      if (r.text.includes('Before you finish: is there a reusable')) tags.push('reminder');
      if (r.text.includes(OLD)) tags.push('old');
      if (r.text.includes('OWNER-NEXT-336')) tags.push('owner-next');
      return tags.join('+');
    }),
    handoverFrames,
    ...(followed ? { follow: followed } : {}),
    agentLog: logs.split('\n').filter(l => /context|handover|Context|\[fault\]|Conversation|Transcript:/.test(l)).slice(0, 20)
  };
  say('RESULT ' + JSON.stringify(result));
  try { process.kill(-proc.pid, 'SIGTERM'); } catch {}
  await sleep(500);
  process.exit(0);
})().catch(error => { say('ERROR ' + (error && error.stack || error)); process.exit(1); });
