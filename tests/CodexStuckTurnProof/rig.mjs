// #562 rig: real pinned Codex TUI in the real common/server.js PTY (under proot, branch runtime as
// /opt/session-agent, branch requirements.toml as /etc/codex), a stand-in hub that allows every
// tool and keeps an inbox, and a scripted mock Responses model that can start a background
// process through unified exec, stall its stream, or just answer.
//
// The model script, keyed by the latest user text:
//   "BG:<cmd>"     -> exec_command {cmd, yield_time_ms: 1000}; after its output: text "STARTED <cmd>"
//   "FG:<cmd>"     -> exec_command {cmd} with the CLI's default yield; then text
//   "STALL"        -> response.created, then nothing (connection stays open)
//   "STALLTOOL"    -> exec_command "sleep 1"; the request carrying its output stalls
//   anything else  -> text "ACK <MSG-ids>"
import http from 'node:http';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

const R = path.dirname(new URL(import.meta.url).pathname);
const S = path.dirname(R);
const require = createRequire(R + '/rt/package.json');
const { Terminal } = require('@xterm/headless');
const WebSocket = require('ws');

const HUB = 18400 + Math.floor(Math.random() * 500);
const AGENT = Number(process.env.AGENT_PORT || HUB + 1000);
const MODEL = HUB + 2000;
const OUT = process.env.OUT || R + '/out-' + (process.env.SCENARIO || 'bg');
fs.mkdirSync(OUT, { recursive: true });
const log = fs.createWriteStream(OUT + '/rig.log');
const t0 = Date.now();
const say = (...a) => { const line = `[${((Date.now() - t0) / 1000).toFixed(1)}s] ` + a.join(' '); console.log(line); log.write(line + '\n'); };

const inbox = [];
const taken = [];
const modelRequests = [];
const hubEvents = [];
const openStreams = new Set();
const peerPolls = [];
let lastPrompt = '';
let activeStart = 0;

const sse = events => events.map(e => `event: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`).join('');
function lastItem(body) {
  const items = Array.isArray(body.input) ? body.input : [];
  return items[items.length - 1] || {};
}
function lastUserText(body) {
  const items = Array.isArray(body.input) ? body.input : [];
  for (let i = items.length - 1; i >= 0; i--) {
    const item = items[i];
    if (item.type === 'message' && item.role === 'user') return (item.content || []).map(p => p.text || '').join('');
  }
  return '';
}
const completed = n => ({ type: 'response.completed', response: { id: 'r' + n, usage: { input_tokens: 0, input_tokens_details: null, output_tokens: 0, output_tokens_details: null, total_tokens: 0 } } });
const textItem = (n, text) => ({ type: 'response.output_item.done', item: { type: 'message', role: 'assistant', id: 'm' + n, content: [{ type: 'output_text', text }] } });
const callItem = (n, name, args) => ({ type: 'response.output_item.done', item: { type: 'function_call', id: 'fc' + n, call_id: 'call_' + n, name, arguments: JSON.stringify(args) } });

function model(parsed, sink) {
  const n = modelRequests.length + 1;
  // Over the WebSocket transport a follow-up carries only the new items (previous_response_id),
  // so a tool output arrives without the prompt it belongs to.
  let user = lastUserText(parsed);
  const last = lastItem(parsed);
  if (!user && last.type === 'function_call_output') user = lastPrompt;
  else if (user && !/^Generate a concise, single-line task title/.test(user)) lastPrompt = user;
  const isTitle = /^Generate a concise, single-line task title/.test(user);
  modelRequests.push({ at: Date.now(), user, lastType: last.type, isTitle, body: parsed });
  if (n === 1) fs.writeFileSync(OUT + '/tools-raw.json', JSON.stringify(parsed.tools, null, 1));
  if (n === 1) fs.writeFileSync(OUT + '/tools.json', JSON.stringify((parsed.tools || []).map(t => ({ name: t.name || t.type, params: t.parameters && Object.keys(t.parameters.properties || {}) })), null, 1));
  const lastOut = last.type === 'function_call_output' ? String(last.output).slice(0, 300) : '';
  say('MODEL #' + n + ' via ' + sink.kind + (isTitle ? ' (title)' : '') + ' last=' + last.type + ' user=' + JSON.stringify(user.slice(-200)) + (lastOut ? ' toolOutput=' + JSON.stringify(lastOut) : ''));
  fs.appendFileSync(OUT + '/model.log', JSON.stringify({ n, at: Date.now() - t0, via: sink.kind, lastType: last.type, user: user.slice(-500), lastOut }) + '\n');
  sink.send([{ type: 'response.created', response: { id: 'r' + n } }]);
  const finish = evs => { sink.send(evs); sink.end(); };
  const stall = why => { openStreams.add(sink); say('MODEL #' + n + ' ' + why); sink.onClose(() => { if (openStreams.delete(sink)) say('MODEL #' + n + ' stalled stream closed by the client after ' + ((Date.now() - stallStart) / 1000).toFixed(1) + 's'); }); const stallStart = Date.now(); };
  if (isTitle) return finish([textItem(n, 'Rig title'), completed(n)]);
  const bg = user.match(/BG:(.+)$/m), fg = user.match(/FG:(.+)$/m), bgt = user.match(/BGT:(.+)$/m);
  const yld = user.match(/YIELD:(\d+):(.+)$/m), poll = user.match(/POLL:(\d+):(.+)$/m);
  if (/ACTIVE/.test(user) && last.type === 'function_call_output') {
    const sid = (String(last.output).match(/session ID (\d+)/) || [])[1];
    if (sid && Date.now() - activeStart < Number(process.env.ACTIVE_MS || 240000)) return finish([callItem(n, 'write_stdin', { session_id: Number(sid), chars: '', yield_time_ms: Number(process.env.POLL_YIELD_MS || 20000) }), completed(n)]);
    return finish([textItem(n, 'Build finished. ACK none'), completed(n)]);
  }
  if (/ACTIVE/.test(user)) { activeStart = Date.now(); return finish([callItem(n, 'exec_command', { cmd: 'for i in $(seq 1 100000); do echo build step $i; sleep 5; done', yield_time_ms: 1000 }), completed(n)]); }
  if (/STREAMTEXT|STREAMREASON/.test(user) && last.type !== 'function_call_output') {
    const reason = /STREAMREASON/.test(user);
    const id = 'm' + n; const total = Number(process.env.STREAM_MS || 180000), every = Number(process.env.STREAM_EVERY_MS || 10000);
    if (reason) sink.send([{ type: 'response.output_item.added', output_index: 0, item: { type: 'reasoning', id: 'rs' + n, summary: [] } }]);
    else sink.send([{ type: 'response.output_item.added', output_index: 0, item: { type: 'message', role: 'assistant', id, content: [] } }]);
    let k = 0, text = '';
    const iv = setInterval(() => {
      k++;
      if (reason) sink.send([{ type: 'response.reasoning_summary_text.delta', item_id: 'rs' + n, output_index: 0, summary_index: k - 1, delta: '**Checking part ' + k + '**\n\nLooking at module ' + k + '.' }, { type: 'response.reasoning_summary_part.added', item_id: 'rs' + n, output_index: 0, summary_index: k }]);
      else { const d = 'Line ' + k + ' of a long streamed answer.\n'; text += d; sink.send([{ type: 'response.output_text.delta', item_id: id, output_index: 0, content_index: 0, delta: d }]); }
      if (k * every >= total) {
        clearInterval(iv);
        const done = reason ? [{ type: 'response.output_item.done', output_index: 0, item: { type: 'reasoning', id: 'rs' + n, summary: [{ type: 'summary_text', text: 'done' }] } }, textItem(n, 'Done thinking. ACK none')] : [{ type: 'response.output_item.done', output_index: 0, item: { type: 'message', role: 'assistant', id, content: [{ type: 'output_text', text }] } }];
        finish([...done, completed(n)]);
      }
    }, every);
    return;
  }
  if (poll && last.type === 'function_call_output') {
    const items = parsed.input || [];
    let lu = -1; items.forEach((i, k) => { if (i.type === 'message' && i.role === 'user') lu = k; });
    const outputs = items.slice(lu + 1).filter(i => i.type === 'function_call_output');
    const sid = (String(last.output).match(/session ID (\d+)/) || [])[1];
    if (outputs.length === 1 && sid) return finish([callItem(n, 'write_stdin', { session_id: Number(sid), chars: '', yield_time_ms: Number(poll[1]) }), completed(n)]);
  }
  if (last.type === 'function_call_output') {
    if (/STALLTOOL/.test(user)) {
      if (process.env.KEEPALIVE_MS) { const iv = setInterval(() => { if (!openStreams.has(sink)) return clearInterval(iv); sink.send([{ type: process.env.KEEPALIVE_TYPE || 'response.in_progress', response: { id: 'r' + n } }]); }, Number(process.env.KEEPALIVE_MS)); }
      return stall('stalls after the tool output' + (process.env.KEEPALIVE_MS ? ' (keep-alive events)' : ''));
    }
    return finish([textItem(n, 'STARTED ' + ((bg || fg || [])[1] || '') + ' - it keeps running in the background. ACK ' + (user.match(/MSG-\w+/g) || ['none']).join(',')), completed(n)]);
  }
  if (/STALLTOOL/.test(user)) return finish([textItem(n, 'I can set up the first technical run with generated demo audio.'), callItem(n, 'exec_command', { cmd: process.env.STALL_CMD || 'sleep 1', yield_time_ms: 1000 }), completed(n)]);
  if (/STALL/.test(user) && !/AgentHub|MSG-/.test(user)) {
    if (process.env.KEEPALIVE_MS) { const iv = setInterval(() => { if (!openStreams.has(sink)) return clearInterval(iv); sink.send([{ type: process.env.KEEPALIVE_TYPE || 'response.in_progress', response: { id: 'r' + n } }]); }, Number(process.env.KEEPALIVE_MS)); }
    if (process.env.PING_MS && sink.ping) { const iv = setInterval(() => { if (!openStreams.has(sink)) return clearInterval(iv); sink.ping(); }, Number(process.env.PING_MS)); }
    return stall('stalls (no further events)' + (process.env.PING_MS && sink.ping ? ', pinging every ' + process.env.PING_MS + 'ms' : ''));
  }
  const mcp = user.match(/MCP:(\S+)(?: (\{.*\}))?/);
  if (mcp) { const [ns, name] = mcp[1].split('/'); const c = callItem(n, name, mcp[2] ? JSON.parse(mcp[2]) : {}); c.item.namespace = ns; return finish([c, completed(n)]); }
  const tfg = user.match(/TEXTFG:(.+)$/m), tbgt = user.match(/TEXTBGT:(.+)$/m);
  if (tfg || tbgt) { const c = callItem(n, 'exec_command', { cmd: (tfg || tbgt)[1].trim(), ...(tbgt ? { tty: true } : {}) }); return finish([textItem(n, 'I can set up the first run with generated demo audio.'), c, completed(n)]); }
  if (yld) return finish([callItem(n, 'exec_command', { cmd: yld[2].trim(), yield_time_ms: Number(yld[1]) }), completed(n)]);
  if (poll) return finish([callItem(n, 'exec_command', { cmd: poll[2].trim(), yield_time_ms: 1000 }), completed(n)]);
  if (bgt) return finish([callItem(n, 'exec_command', { cmd: bgt[1].trim(), yield_time_ms: 1000, tty: true }), completed(n)]);
  if (bg) return finish([callItem(n, 'exec_command', { cmd: bg[1].trim(), yield_time_ms: 1000 }), completed(n)]);
  if (fg) return finish([callItem(n, 'exec_command', { cmd: fg[1].trim() }), completed(n)]);
  return finish([textItem(n, 'ACK ' + (user.match(/MSG-\w+/g) || ['none']).join(',')), completed(n)]);
}

const handler = (req, res) => {
  let body = '';
  req.on('data', c => { body += c; });
  req.on('end', async () => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname === '/v1/responses' && req.method === 'POST') {
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      model(JSON.parse(body), { send: evs => res.write(sse(evs)), end: () => res.end(), onClose: f => res.on('close', f), destroy: () => res.destroy(), kind: 'sse' });
      return;
    }
    if (url.pathname.startsWith('/v1/') && url.pathname !== '/v1/responses') {
      say('MODEL other endpoint ' + req.method + ' ' + url.pathname + ' ' + body.slice(0, 200) + (process.env.HANG_OTHER ? ' -> hanging' : ''));
      if (process.env.HANG_OTHER) { openStreams.add(res); res.on('close', () => { if (openStreams.delete(res)) say('MODEL other endpoint closed by the client'); }); return; }
      res.writeHead(404); res.end(); return;
    }
    // A child session that runs on: an interactive session stays Running until someone stops it.
    if (url.pathname.startsWith('/internal/sessions/s1/peer/') && req.method === 'GET') {
      const id = decodeURIComponent(url.pathname.split('/').pop());
      peerPolls.push(Date.now());
      if (peerPolls.length === 1 || peerPolls.length % 50 === 0) say('HUB peer poll #' + peerPolls.length + ' for ' + id);
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ id, title: 'Child', phase: 'Running', mode: 'Interactive', agent: 'Codex' }));
      return;
    }
    if (url.pathname === '/internal/sessions/s1/agent-policy') { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{"decision":"allow"}'); return; }
    if (url.pathname === '/internal/sessions/s1/messages' && req.method === 'GET') {
      const via = url.searchParams.get('via') === 'next_turn' ? 'next_turn' : 'inbox';
      const batch = inbox.splice(0, 4);
      for (const m of batch) taken.push({ id: m.id, via, at: Date.now() });
      if (batch.length) say('HUB take via=' + via + ': ' + batch.map(m => m.id).join(','));
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ messages: batch.map(m => ({ id: m.id, from: m.from, fromTitle: m.fromTitle, body: m.body, priority: m.priority })) }));
      return;
    }
    hubEvents.push({ at: Date.now() - t0, path: url.pathname, body: body.slice(0, 300) });
    if (!/usage|transcript|state/.test(url.pathname)) say('HUB ' + req.method + ' ' + url.pathname + ' ' + body.slice(0, 200));
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end('{}');
  });
};
const server = http.createServer(handler);
// The model endpoint listens on its own port so a scenario can take it away (connection refused).
const modelSockets = new Set();
let modelServer;
function startModel() {
  modelServer = http.createServer(handler);
  modelServer.on('connection', s => { modelSockets.add(s); s.on('close', () => modelSockets.delete(s)); });
  modelServer.on('upgrade', (...a) => server.emit('upgrade', ...a));
  modelServer.listen(MODEL, '127.0.0.1');
}
function stopModel() { modelServer.close(); for (const s of modelSockets) s.destroy(); say('MODEL endpoint DOWN (connection refused from now on)'); }
startModel();
// The Responses WebSocket transport: one JSON text frame per event, a response.create per turn
// request on a connection the CLI keeps open.
const wss = new WebSocket.WebSocketServer({ noServer: true });
server.on('upgrade', (req, socket, head) => {
  say('WS upgrade ' + req.url);
  wss.handleUpgrade(req, socket, head, conn => {
    conn.on('message', data => {
      let msg; try { msg = JSON.parse(data.toString()); } catch { return; }
      if (msg.type !== 'response.create') { say('WS frame ' + msg.type); return; }
      const { type, ...body } = msg;
      model(body, { send: evs => evs.forEach(e => conn.send(JSON.stringify(e))), end: () => {}, onClose: f => conn.on('close', f), destroy: () => conn.terminate(), ping: () => conn.ping(), kind: 'ws' });
    });
  });
});
server.listen(HUB, '127.0.0.1');

const home = R + '/home';
fs.rmSync(home, { recursive: true, force: true });
fs.mkdirSync(home + '/.codex', { recursive: true });
fs.writeFileSync(home + '/.codex/config.toml', [
  'cli_auth_credentials_store = "file"',
  'model = "gpt-5.4"',
  'model_provider = "rig"',
  ...(process.env.EXTRA_TOML ? process.env.EXTRA_TOML.split('\\n') : []),
  `[model_providers.rig]`, 'name = "rig"', `base_url = "http://127.0.0.1:${MODEL}/v1"`,
  'wire_api = "responses"', 'env_key = "CODEX_API_KEY"', `supports_websockets = ${process.env.WS ? 'true' : 'false'}`,
  `request_max_retries = 0`, `stream_max_retries = ${process.env.STREAM_RETRIES ?? 0}`,
  ...(process.env.IDLE_MS ? [`stream_idle_timeout_ms = ${process.env.IDLE_MS}`] : []),
  `[projects."${R}/work"]`, 'trust_level = "trusted"', ''
].join('\n'));
const env = {
  PATH: `${S}/clis/node_modules/.bin:/usr/local/bin:/usr/bin:/bin`,
  HOME: home, CODEX_HOME: home + '/.codex', TMPDIR: '/tmp', TERM: 'xterm-256color', LANG: 'C.UTF-8',
  CODEX_API_KEY: 'rig-key',
  AGENTHUB_DRIVER: '/opt/session-agent/codex/driver.js', AGENTHUB_MODE: process.env.MODE || 'interactive', AGENTHUB_PROMPT: process.env.PROMPT || '', AGENTHUB_PORT: String(AGENT),
  AGENTHUB_SESSION_ID: 's1', AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${HUB}/internal/sessions/s1`,
  AGENTHUB_CALLBACK_TOKEN: 'rig-token', AGENTHUB_STALLED_TURN_NOTICE_MINUTES: process.env.NOTICE_MIN ?? '', AGENTHUB_STALLED_TURN_INTERRUPT_MINUTES: process.env.INTERRUPT_MIN ?? '', AGENTHUB_WORKDIR: R + '/work', AGENTHUB_SKILLS_SYNC_SECONDS: '0'
};
const proc = spawn(S + '/proot', ['-b', R + '/rt:/opt/session-agent', '-b', R + '/etc-codex:/etc/codex', '-b', R + '/etc-opencode:/etc/opencode', '-b', fs.mkdtempSync(R + '/tmp-run-') + ':/tmp',
  'node', '/opt/session-agent/server.js'], { env, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
proc.stdout.on('data', d => log.write('[sa] ' + d));
proc.on('exit', c => say('session agent exited ' + c));
proc.stderr.on('data', d => log.write('[sa!] ' + d));

const term = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
let ws;
const activity = [];
const screen = () => { const b = term.buffer.active; const rows = []; for (let i = 0; i < term.rows; i++) rows.push(b.getLine(b.viewportY + i)?.translateToString(true) ?? ''); return rows.join('\n'); };
const dump = name => { fs.writeFileSync(`${OUT}/screen-${name}.txt`, screen()); say('dumped screen-' + name + ' | ' + (screen().match(/.*(Working|esc to interrupt|background terminal|Messages to be submitted|warning).*/g) || []).map(s => s.trim()).join(' || ')); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, ms, what) { const end = Date.now() + ms; while (Date.now() < end) { if (fn()) return true; await sleep(200); } say('TIMEOUT waiting for ' + what); return false; }
const sendInput = data => ws.send(JSON.stringify({ type: 'input', data }));
async function paste(text) { sendInput('\x1b[200~' + text + '\x1b[201~'); await sleep(200); sendInput('\r'); }
const curState = () => activity.length ? activity[activity.length - 1].state : '?';

async function push(id, body, extra = {}) {
  const m = { id, from: 'owner', fromTitle: 'Owner', body, priority: false, ...extra };
  inbox.push(m);
  const res = await fetch(`http://127.0.0.1:${AGENT}/agenthub/messages`, { method: 'POST', headers: { 'X-Agent-Token': 'rig-token', 'Content-Type': 'application/json' }, body: JSON.stringify(m) });
  const answer = await res.json();
  if (['pty', 'chat', 'queued-for-mod'].includes(answer.delivered)) { const i = inbox.indexOf(m); if (i >= 0) inbox.splice(i, 1); taken.push({ id, via: answer.delivered, at: Date.now() }); }
  say('SEND ' + id + JSON.stringify(extra) + ' -> ' + JSON.stringify(answer));
  return answer;
}
const modelSaw = id => modelRequests.find(r => !r.isTitle && JSON.stringify(r.body.input).includes(id));
const report = (label, ids) => say('RESULT ' + label + ': activity=' + curState() + ' ' + ids.map(id => id + '=' + (modelSaw(id) ? 'delivered' : 'NOT delivered')).join(' ') + ' inbox=' + inbox.map(m => m.id).join(',') + ' takes=' + JSON.stringify(taken.map(t => t.id + ':' + t.via)));

async function f2(name) { sendInput('\x1bOQ'); await sleep(1500); dump(name + '-f2'); sendInput('\x1b'); await sleep(800); }

async function main() {
  await sleep(1500);
  ws = new WebSocket(`ws://127.0.0.1:${AGENT}/`);
  await new Promise((r, j) => { ws.once('open', r); ws.once('error', j); });
  ws.on('message', d => term.write(d.toString()));
  term.onData(d => sendInput(d));
  const aws = new WebSocket(`ws://127.0.0.1:${AGENT}/`);
  await new Promise((r, j) => { aws.once('open', r); aws.once('error', j); });
  aws.send(JSON.stringify({ type: 'subscribe', channel: 'activity' }));
  aws.on('message', d => { const s = d.toString(); if (s.startsWith('{"type":"agenthub"')) { const f = JSON.parse(s); if (f.subtype === 'activity') { activity.push({ at: Date.now() - t0, state: f.state, stalled: f.stalled }); say('ACTIVITY ' + f.state + (f.reason ? ' ' + f.reason : '') + (f.stalled ? ' STALLED ' + JSON.stringify(f.stalled) : '')); } } });
  const scenario = process.env.SCENARIO || 'bg';
  if (process.env.MODE === 'autonomous') {
    await sleep(Number(process.env.WATCH_MS || 30000)); dump('auto-1');
    say('AUTO: sending Ctrl+C'); sendInput('\x03'); await sleep(8000); dump('auto-2');
    say('AUTO RESULT: exited=' + proc.exitCode); return;
  }
  await until(() => /context left|Ask Codex|›/.test(screen()), 60000, 'agent ready');
  await sleep(1500);
  dump('00-ready');
  await f2('00-ready');

  if (scenario === 'bg') {
    // a) a never-ending process through unified exec, then the model answers.
    const cmd = process.env.CMD || 'sleep infinity';
    await paste('Please start it. BG:' + cmd);
    await until(() => modelRequests.some(r => r.lastType === 'function_call_output'), 30000, 'tool output at the model');
    await sleep(Number(process.env.OBSERVE_MS || 20000));
    dump('a1-after-answer');
    await f2('a1');
    // c) messages while in that state.
    await push('MSG-N', 'MSG-N: normal Workspace message.');
    await sleep(8000);
    report('a/normal', ['MSG-N']);
    await paste('MSG-Q composer message typed while it runs');
    await sleep(8000);
    dump('a2-composer');
    report('a/composer', ['MSG-N', 'MSG-Q']);
    await push('MSG-P', 'MSG-P: priority Workspace message.', { priority: true });
    await sleep(8000);
    dump('a3-priority');
    report('a/priority', ['MSG-N', 'MSG-Q', 'MSG-P']);
    ws.send(JSON.stringify({ type: 'interrupt' }));
    await sleep(8000);
    dump('a4-after-interrupt');
    report('a/interrupt', ['MSG-N', 'MSG-Q', 'MSG-P']);
    sendInput('/ps'); await sleep(400); sendInput('\r'); await sleep(2500);
    dump('a5-ps');
    return;
  }
  if (scenario === 'serve') {
    // For the browser proof: start the owner's stall, then let the web app do the rest.
    await paste(process.env.SERVE_PROMPT || 'Please do the STALLTOOL task');
    fs.writeFileSync(OUT + '/ready', String(AGENT));
    const end = Date.now() + Number(process.env.HOLD_MS || 300000);
    while (Date.now() < end && !fs.existsSync(OUT + '/stop')) await sleep(1000);
    dump('serve-end');
    say('SERVE RESULT: user texts the model saw after the stall: ' + JSON.stringify(modelRequests.filter(r => !r.isTitle && r.user).map(r => r.user.slice(-120))) + ' stalled frames=' + activity.filter(a => a.stalled).length);
    return;
  }
  if (scenario === 'active') {
    // Negative control: a long turn that keeps doing things must never be reported as stalled.
    await paste(process.env.ACTIVE_PROMPT || 'Please run the ACTIVE build');
    const total = Number(process.env.ACTIVE_MS || process.env.STREAM_MS || 240000);
    for (let i = 1; i * 60000 <= total + 60000; i++) { await sleep(60000); dump('active-' + i); }
    await until(() => curState() === 'idle', 120000, 'turn end');
    dump('active-end');
    say('ACTIVE RESULT: stalled frames=' + activity.filter(a => a.stalled).length + ' activity states=' + JSON.stringify([...new Set(activity.map(a => a.state))]) + ' model requests=' + modelRequests.filter(r => !r.isTitle).length);
    return;
  }
  if (scenario === 'idlebg') {
    await paste('Please start it. BG:' + (process.env.CMD || 'sleep infinity'));
    await until(() => modelRequests.some(r => r.lastType === 'function_call_output'), 30000, 'tool output');
    await sleep(5000);
    const sent = Date.now();
    await push('MSG-N', 'MSG-N: normal message to an idle agent with a background terminal.');
    const ok = await until(() => modelSaw('MSG-N'), Number(process.env.WAIT_MS || 60000), 'MSG-N');
    dump('idlebg');
    say('IDLEBG RESULT: MSG-N ' + (ok ? 'delivered after ' + ((modelSaw('MSG-N').at - sent) / 1000).toFixed(1) + 's' : 'NOT delivered') + ' inbox=' + inbox.map(m => m.id));
    return;
  }
  if (scenario === 'down') {
    // The model endpoint becomes unreachable (connection refused) and comes back after DOWN_MS.
    await paste('hello MSG-H');
    await until(() => modelSaw('MSG-H'), 20000, 'hello');
    await sleep(2000);
    stopModel();
    const downAt = Date.now();
    await paste('Please answer this while the network is down MSG-D');
    const watch = Number(process.env.WATCH_MS || 120000);
    for (let i = 1; Date.now() - downAt < watch; i++) { await sleep(Math.min(30000, watch / 4)); dump('down-' + i); }
    await paste('MSG-Q composer message typed while down');
    await sleep(3000);
    dump('down-queued');
    await push('MSG-N', 'MSG-N: normal Workspace message while down.');
    report('down/while-down', ['MSG-D', 'MSG-Q', 'MSG-N']);
    if (process.env.RESTORE) {
      startModel(); say('MODEL endpoint back'); const backAt = Date.now(); until(() => modelSaw('MSG-D'), 600000, 'x').then(ok => say('RECOVERY: MSG-D reached the model ' + (ok ? ((modelSaw('MSG-D').at - backAt) / 1000).toFixed(1) + 's after the endpoint came back' : 'never')));
      await sleep(Number(process.env.RESTORE_WATCH_MS || 60000));
      dump('down-restored');
      report('down/after-restore', ['MSG-D', 'MSG-Q', 'MSG-N']);
    }
    ws.send(JSON.stringify({ type: 'interrupt' }));
    await sleep(5000);
    dump('down-after-esc');
    report('down/after-esc', ['MSG-D', 'MSG-Q', 'MSG-N']);
    return;
  }
  if (scenario === 'compact') {
    await paste('hello MSG-H');
    await until(() => modelSaw('MSG-H'), 20000, 'hello');
    await sleep(2000);
    await paste('/compact');
    await sleep(Number(process.env.WATCH_MS || 30000));
    dump('compact');
    await paste('MSG-Q queued after compact');
    await sleep(8000);
    dump('compact-queued');
    report('compact', ['MSG-Q']);
    return;
  }
  if (scenario === 'cmds') {
    // Which commands keep a turn in its tool call: per command, time from the prompt to the
    // tool output reaching the model, and the screen afterwards.
    const out = [];
    for (const c of (process.env.CMDS || '').split(';;').filter(Boolean)) {
      const before = modelRequests.length;
      const t = Date.now();
      await paste('Run it. ' + c);
      setTimeout(() => dump('cmd-' + out.length + '-mid'), 5000);
      const need = /POLL:/.test(c) ? 2 : 1;
      
      const ok = await until(() => modelRequests.slice(before).filter(r => r.lastType === 'function_call_output').length >= need, Number(process.env.CMD_WAIT_MS || 60000), 'tool output for ' + c);
      await sleep(3000);
      dump('cmd-' + out.length);
      const line = c + ' => ' + (ok ? 'tool returned after ' + ((modelRequests.slice(before).filter(r => r.lastType === 'function_call_output')[need - 1].at - t) / 1000).toFixed(1) + 's' : 'TOOL STILL RUNNING') + ', activity=' + curState() + ', screen: ' + (screen().match(/.*(Working|background terminal).*/g) || []).map(x => x.trim()).join(' | ');
      out.push(line); say('CMD ' + line);
      if (!ok) { await paste('MSG-Q steer typed while the tool runs'); await sleep(3000); dump('cmd-' + (out.length - 1) + '-queued'); await push('MSG-N', 'MSG-N: Workspace message while the tool runs.'); await sleep(5000); report('cmd/before-esc', ['MSG-Q', 'MSG-N']); ws.send(JSON.stringify({ type: 'interrupt' })); await sleep(5000); dump('cmd-' + (out.length - 1) + '-after-esc'); await sleep(5000); report('cmd/after-esc', ['MSG-Q', 'MSG-N']); dump('cmd-' + (out.length - 1) + '-after-esc2'); say('CMD after Esc: activity=' + curState() + ' screen: ' + (screen().match(/.*(Working|background terminal|nterrupt).*/g) || []).map(x => x.trim()).join(' | ')); }
    }
    say('CMDS RESULT\n  ' + out.join('\n  '));
    return;
  }
  if (scenario === 'stall' || scenario === 'stalltool') {
    // b) the model stream stalls with the connection open.
    await paste(scenario === 'stall' ? 'Please do the STALL task' : 'Please do the STALLTOOL task');
    await until(() => openStreams.size > 0, 30000, 'stalled stream');
    const stallAt = Date.now();
    await sleep(15000);
    dump('b1-stalled');
    await f2('b1');
    await paste('MSG-Q composer message typed during the stall');
    await sleep(5000);
    dump('b2-queued');
    await push('MSG-N', 'MSG-N: normal Workspace message during the stall.');
    await sleep(5000);
    report('b/normal+composer', ['MSG-N', 'MSG-Q']);
    const watch = Number(process.env.WATCH_MS || 30000);
    await until(() => openStreams.size === 0, watch, 'the CLI to give up on the stalled stream');
    say('STALL watched ' + ((Date.now() - stallAt) / 1000).toFixed(0) + 's; stream still open=' + (openStreams.size > 0));
    dump('b3-after-watch');
    report('b/after-watch', ['MSG-N', 'MSG-Q']);
    const st = activity.find(a => a.stalled);
    say('STALL NOTICE: ' + (st ? 'first stalled frame ' + ((st.at - (stallAt - t0)) / 1000).toFixed(0) + 's after the stall began: ' + JSON.stringify(st.stalled) : 'none') + '; last frame ' + JSON.stringify(activity[activity.length - 1]));
    if (openStreams.size) {
      await push('MSG-P', 'MSG-P: priority during the stall.', { priority: true });
      await sleep(8000);
      report('b/priority', ['MSG-N', 'MSG-Q', 'MSG-P']);
      if (process.env.INTERRUPT_PUSH) await push('MSG-X', 'MSG-X: priority with interrupt during the stall.', { priority: true, interrupt: true });
      else ws.send(JSON.stringify({ type: 'interrupt' }));
      await sleep(8000);
      dump('b4-after-interrupt');
      report('b/interrupt', ['MSG-N', 'MSG-Q', 'MSG-P', 'MSG-X']);
    }
    await sleep(8000);
    dump('b5-end');
    report('b/end', ['MSG-N', 'MSG-Q', 'MSG-P']);
    return;
  }
}

main().catch(e => say('ERROR ' + e.stack)).finally(async () => {
  await sleep(500);
  for (const r of openStreams) try { r.destroy(); } catch {}
  try { process.kill(-proc.pid, 'SIGTERM'); } catch {}
  await sleep(1500);
  try { process.kill(-proc.pid, 'SIGKILL'); } catch {}
  process.exit(0);
});
