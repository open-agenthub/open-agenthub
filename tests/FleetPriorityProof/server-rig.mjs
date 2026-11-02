// #390 proof through the REAL session agent: <runtime>/session-agent/server.js runs under proot
// with the given agent-runtime tree as /opt/session-agent and a private /tmp, so the real
// claude driver starts the real Claude Code with the real agenthub-fleet mod from that tree.
// A stand-in hub records what SessionMessageDelivery would do with each answer (queued-for-mod
// and pty mark the row delivered, anything else leaves it for agent_inbox), and a mock Messages
// API keeps the initial task busy with STEPS Bash calls of SLEEP_S seconds each.
//
// Usage: node server-rig.mjs <agent-runtime dir> <rig dir>
//   env MODE=interactive|autonomous  CASE=busy|lastanswer  STEPS=3 SLEEP_S=12 WAIT_S=90
//   busy:       the priority message is sent while the first tool call runs
//   lastanswer: it is sent while the turn's final answer streams (FINAL_MS=8000)
//   DENY_APPEND=1 loads a second plugin that refuses every plugin append, as an organisation's
//   policy mod could: the message must then stay in the hub inbox.
// Needs proot at $PROOT (default: <rig dir>/../proot) and session-agent node_modules built.
import http from 'node:http';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const RUNTIME = path.resolve(process.argv[2]);
const R = path.resolve(process.argv[3]);
const PROOT = process.env.PROOT || path.join(path.dirname(R), 'proot');
const MODE = process.env.MODE || 'interactive';
const CASE = process.env.CASE || 'busy';
const STEPS = Number(process.env.STEPS || 3), SLEEP_S = Number(process.env.SLEEP_S || 12);
const WAIT_S = Number(process.env.WAIT_S || 90), FINAL_MS = Number(process.env.FINAL_MS || 8000);
const HUB = 18400 + Math.floor(Math.random() * 500), AGENT = HUB + 1000;
const MARK = 'MSG-390';
const t0 = Date.now();
fs.mkdirSync(R, { recursive: true });
const logFile = fs.createWriteStream(path.join(R, 'rig.log'));
const say = (...a) => { const line = ((Date.now() - t0) / 1000).toFixed(1).padStart(6) + 's ' + a.join(' '); console.log(line); logFile.write(line + '\n'); };

// ---- runtime copy: /opt/session-agent as the image lays it out -------------------------------
const rt = path.join(R, 'rt');
fs.rmSync(rt, { recursive: true, force: true });
fs.mkdirSync(rt, { recursive: true });
for (const dir of ['common', 'claude', 'files', 'browser']) {
  if (fs.existsSync(path.join(RUNTIME, dir))) fs.cpSync(path.join(RUNTIME, dir), path.join(rt, dir), { recursive: true });
}
fs.copyFileSync(path.join(RUNTIME, 'session-agent/server.js'), path.join(rt, 'server.js'));
fs.copyFileSync(path.join(RUNTIME, 'session-agent/package.json'), path.join(rt, 'package.json'));
fs.cpSync(path.join(RUNTIME, 'session-agent/node_modules'), path.join(rt, 'node_modules'), { recursive: true, dereference: true });
// node-pty does not build in a session pod; the image's own build is used.
fs.rmSync(path.join(rt, 'node_modules/node-pty'), { recursive: true, force: true });
fs.cpSync('/opt/session-agent/node_modules/node-pty', path.join(rt, 'node_modules/node-pty'), { recursive: true });
// 7681 is the pod's real session agent; the mod must poll this rig's.
if (process.env.DENY_APPEND) {
  const deny = path.join(rt, 'claude/mods/rig-deny-append');
  fs.mkdirSync(path.join(deny, '.claude-plugin'), { recursive: true });
  fs.mkdirSync(path.join(deny, 'hooks'), { recursive: true });
  fs.writeFileSync(path.join(deny, '.claude-plugin/plugin.json'), JSON.stringify({ name: 'rig-deny-append', version: '0.0.1', description: 'refuses plugin appends' }));
  fs.writeFileSync(path.join(deny, 'hooks/hooks.json'), JSON.stringify({ description: 'refuses plugin appends', modules: ['./register.js'] }));
  fs.writeFileSync(path.join(deny, 'hooks/register.js'),
    "export function register(on) {\n  on('session.append', { door: 'note' }, () => ({ deny: 'refused by the #390 rig policy' }))\n}\n");
}
const register = path.join(rt, 'claude/mods/agenthub-fleet/hooks/register.js');
fs.writeFileSync(register, fs.readFileSync(register, 'utf8').replaceAll('127.0.0.1:7681', '127.0.0.1:' + AGENT));

// ---- stand-in hub + mock model ----------------------------------------------------------------
const inbox = [];       // undelivered rows, as agent_inbox would find them
const requests = [];    // main-loop model requests: { at, has }
let mainCalls = 0, toolStarted = 0, finalStarted = 0, turnEnded = 0;

function stream(res, msg, blocks, stop, delayMs = 0) {
  res.writeHead(200, { 'content-type': 'text/event-stream' });
  const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n');
  ev('message_start', { message: msg });
  setTimeout(() => {
    blocks.forEach((b, i) => {
      if (b.type === 'text') {
        ev('content_block_start', { index: i, content_block: { type: 'text', text: '' } });
        ev('content_block_delta', { index: i, delta: { type: 'text_delta', text: b.text } });
      } else {
        ev('content_block_start', { index: i, content_block: { type: 'tool_use', id: b.id, name: b.name, input: {} } });
        ev('content_block_delta', { index: i, delta: { type: 'input_json_delta', partial_json: JSON.stringify(b.input) } });
      }
      ev('content_block_stop', { index: i });
    });
    ev('message_delta', { delta: { stop_reason: stop, stop_sequence: null }, usage: { output_tokens: 5 } });
    ev('message_stop', {});
    res.end();
  }, delayMs);
}

http.createServer((req, res) => {
  let body = '';
  req.on('data', c => { body += c; });
  req.on('end', () => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname.startsWith('/v1/messages')) {
      let json = {}; try { json = JSON.parse(body); } catch {}
      const msg = { id: 'msg_' + Date.now(), type: 'message', role: 'assistant', model: json.model || 'm', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } };
      if (url.pathname.includes('count_tokens') || !json.stream) {
        res.writeHead(200, { 'content-type': 'application/json' });
        return res.end(JSON.stringify({ ...msg, input_tokens: 10, content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn' }));
      }
      const text = JSON.stringify(json.messages || []);
      // Title and prompt-suggestion calls carry no tools.
      if (!Array.isArray(json.tools) || !json.tools.length || /Reply with ONLY the suggestion/.test(text)) return stream(res, msg, [{ type: 'text', text: 'ok' }], 'end_turn');
      const has = text.includes(MARK);
      requests.push({ at: Date.now(), has });
      // Counted, not dumped: how often the body and the pointer of the turn-end notice occur.
      const bodies = text.split(MARK + ': priority').length - 1, pointers = text.split('arrived while your last answer').length - 1;
      say('MODEL main request #' + requests.length + (has ? ' CONTAINS ' + MARK + ' (body x' + bodies + ', pointer x' + pointers + ')' : ''));
      if (has && turnEnded) return stream(res, msg, [{ type: 'text', text: 'Read ' + MARK + '.' }], 'end_turn');
      mainCalls++;
      if (mainCalls <= STEPS && !has || mainCalls <= STEPS && CASE === 'busy') {
        toolStarted = toolStarted || Date.now();
        return stream(res, msg, [{ type: 'tool_use', id: 'toolu_' + mainCalls, name: 'Bash', input: { command: 'sleep ' + SLEEP_S, description: 'Long step ' + mainCalls } }], 'tool_use');
      }
      finalStarted = Date.now();
      say('MODEL final answer of the task' + (CASE === 'lastanswer' ? ' (streams for ' + FINAL_MS + ' ms)' : ''));
      const answer = has ? 'Task done; I read ' + MARK + '.' : 'Task done.';
      stream(res, msg, [{ type: 'text', text: answer }], 'end_turn', CASE === 'lastanswer' ? FINAL_MS : 0);
      setTimeout(() => { turnEnded = turnEnded || Date.now(); }, (CASE === 'lastanswer' ? FINAL_MS : 0) + 50);
      return;
    }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    if (url.pathname === '/internal/sessions/s1/messages' && req.method === 'GET') {
      const batch = inbox.splice(0, 4);
      if (batch.length) say('HUB inbox taken: ' + batch.map(m => m.id).join(','));
      return res.end(JSON.stringify({ messages: batch }));
    }
    res.end('{}');
  });
}).listen(HUB, '127.0.0.1');

// ---- session agent ----------------------------------------------------------------------------
const home = path.join(R, 'home'), work = path.join(R, 'work');
fs.rmSync(home, { recursive: true, force: true });
fs.mkdirSync(path.join(home, '.claude'), { recursive: true });
fs.mkdirSync(work, { recursive: true });
const key = 'sk-ant-rig-0000000000000000000000000000';
fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ hasCompletedOnboarding: true,
  customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] }, projects: { [work]: { hasTrustDialogAccepted: true } } }));
fs.writeFileSync(path.join(home, '.claude/settings.json'), JSON.stringify({ permissions: { allow: ['Bash'] } }));
const env = {
  PATH: '/usr/local/bin:/usr/bin:/bin', HOME: home, TMPDIR: '/tmp', TERM: 'xterm-256color', LANG: 'C.UTF-8',
  AGENTHUB_DRIVER: '/opt/session-agent/claude/driver.js', AGENTHUB_MODE: MODE, AGENTHUB_PORT: String(AGENT),
  AGENTHUB_SESSION_ID: 's1', AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${HUB}/internal/sessions/s1`,
  AGENTHUB_CALLBACK_TOKEN: 'rig-token', AGENTHUB_WORKDIR: work, AGENTHUB_SKILLS_SYNC_SECONDS: '0',
  AGENTHUB_PROMPT: 'Do the LONGJOB task.', AGENTHUB_CLAUDE_SESSION_ID: crypto.randomUUID(),
  CLAUDE_CODE_PLUGIN_DIRS: '/opt/session-agent/claude/mods/agenthub-fleet' + (process.env.DENY_APPEND ? ':/opt/session-agent/claude/mods/rig-deny-append' : ''), AGENTHUB_MOD_TOKEN: 'rig-mod-token',
  ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + HUB, DISABLE_AUTOUPDATER: '1'
};
const tmp = fs.mkdtempSync(path.join(R, 'tmp-run-'));
const proc = spawn(PROOT, ['-b', rt + ':/opt/session-agent', '-b', tmp + ':/tmp', 'node', '/opt/session-agent/server.js'],
  { env, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
proc.stdout.on('data', d => logFile.write('[sa] ' + d));
proc.stderr.on('data', d => logFile.write('[sa!] ' + d));
const sleep = ms => new Promise(r => setTimeout(r, ms));
const until = async (fn, ms) => { const end = Date.now() + ms; while (Date.now() < end && !fn()) await sleep(100); return fn(); };

async function send() {
  const m = { id: MARK, from: 'coord-1', fromTitle: 'Coordinator', body: MARK + ': priority status request', priority: true };
  inbox.push(m);
  const started = Date.now();
  const res = await fetch(`http://127.0.0.1:${AGENT}/agenthub/messages`, { method: 'POST',
    headers: { 'X-Agent-Token': 'rig-token', 'Content-Type': 'application/json' }, body: JSON.stringify(m) });
  const answer = await res.json();
  // SessionMessageDelivery: pty/chat/queued-for-mod mark the row delivered; the rest stay.
  if (['pty', 'chat', 'queued-for-mod'].includes(answer.delivered)) inbox.splice(inbox.indexOf(m), 1);
  say('SEND priority -> ' + JSON.stringify(answer) + ' after ' + (Date.now() - started) + ' ms; hub inbox now: [' + inbox.map(x => x.id).join(',') + ']');
  return { answer, at: Date.now() };
}

let result = 'never';
try {
  if (!await until(() => toolStarted, 90000)) throw new Error('the task never started a tool call');
  say('task running (tool call 1 started)');
  let sent;
  if (CASE === 'lastanswer') {
    await until(() => finalStarted, (STEPS + 1) * SLEEP_S * 1000 + 30000);
    await sleep(500);
    sent = await send();
  } else {
    await sleep(3000);
    sent = await send();
  }
  await until(() => requests.some(r => r.has), WAIT_S * 1000);
  const first = requests.find(r => r.has);
  await sleep(4000);
  result = first ? ((first.at - sent.at) / 1000).toFixed(1) + 's after the send' : 'never';
  say('SUMMARY mode=' + MODE + ' case=' + CASE + ' runtime=' + RUNTIME + ' delivered=' + sent.answer.delivered +
    ' modelSaw=' + result + ' turnEnded=' + (turnEnded ? ((turnEnded - sent.at) / 1000).toFixed(1) + 's after the send' : 'not within the wait') +
    ' hubInboxLeft=[' + inbox.map(x => x.id).join(',') + '] mainRequests=' + requests.length);
} catch (error) {
  say('RIG ERROR ' + error.message);
} finally {
  try { process.kill(-proc.pid, 'SIGKILL'); } catch {}
  await sleep(300);
  fs.rmSync(tmp, { recursive: true, force: true });
  process.exit(result === 'never' ? 1 : 0);
}
