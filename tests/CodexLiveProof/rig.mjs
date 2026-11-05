// #452 rig: real pinned Codex TUI in the real common/server.js PTY (proot), a scripted mock
// Responses model that runs a long turn of tool calls (long output, a background terminal), the
// Workspace composer's bytes sent mid-turn, and a poller that follows /agenthub/transcript the
// way the hub's LiveTranscriptReader does.
import http from 'node:http';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';

// RIG_HOME: a scratch directory outside the repository holding clis/ (the pinned CLI), proot,
// and rt/, etc-codex/, etc-opencode/ as build.sh writes them. Everything this run writes goes there.
const R = process.env.RIG_HOME;
if (!R || !path.isAbsolute(R)) throw new Error('set RIG_HOME to an absolute scratch directory');
const S = R;
const require = createRequire(R + '/rt/package.json');
const { Terminal } = require('@xterm/headless');
const WebSocket = require('ws');
const HUB = 18400 + Math.floor(Math.random() * 500);
const AGENT = Number(process.env.AGENT_PORT || HUB + 1000);
const OUT = process.env.OUT || R + '/out';
fs.mkdirSync(OUT, { recursive: true });
const log = fs.createWriteStream(OUT + '/rig.log');
const t0 = Date.now();
// Logged values include what the model and the CLI sent; control characters are escaped so
// one of them cannot start a forged line in rig.log.
const say = (...a) => { const line = `[${((Date.now() - t0) / 1000).toFixed(1)}s] ` + a.join(' ').replace(/[\x00-\x1f\x7f]/g, c => '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0')); console.log(line); log.write(line + '\n'); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const sse = events => events.map(e => `event: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`).join('');

// The turn the model plays: each step answers one request of the WORK turn.
const LONG_LINES = Number(process.env.LONG_LINES || 200000);
const SUB = process.env.SCENARIO === 'sub';
const QUEUE = process.env.SCENARIO === 'queue';
const IMAGE = process.env.SCENARIO === 'image';
if (IMAGE && !fs.existsSync(R + '/noise.png')) writeNoisePng(R + '/noise.png');
const steps = IMAGE ? [
  { call: 'view_image', args: { path: R + '/noise.png' } },
  { wait: 1000, call: 'exec_command', args: { cmd: 'echo after-image', yield_time_ms: 2000 } },
  { wait: 4000, call: 'exec_command', args: { cmd: 'echo step3', yield_time_ms: 2000 } },
  { wait: 4000, text: 'WORK DONE' }
] : QUEUE ? [
  { call: 'exec_command', args: { cmd: 'sleep 12; echo slow-done', yield_time_ms: 15000 } },
  { wait: 2000, call: 'exec_command', args: { cmd: 'echo step2', yield_time_ms: 2000 } },
  { wait: 2000, text: 'WORK DONE' }
] : SUB ? [
  { call: 'spawn_agent', ns: 'multi_agent_v1', args: { message: 'SUBTASK: say hi' } },
  { wait: 2000, call: 'exec_command', args: { cmd: 'echo step2', yield_time_ms: 2000 } },
  { wait: 4000, call: 'exec_command', args: { cmd: 'echo step3', yield_time_ms: 2000 } },
  { wait: 4000, call: 'exec_command', args: { cmd: 'echo step4', yield_time_ms: 2000 } },
  { wait: 4000, call: 'exec_command', args: { cmd: 'echo step5', yield_time_ms: 2000 } },
  { wait: 4000, text: 'WORK DONE' }
] : [
  { call: 'exec_command', args: { cmd: `seq 1 ${LONG_LINES}`, yield_time_ms: 5000 } },
  { call: 'exec_command', args: { cmd: 'sleep 45; echo bg-done', yield_time_ms: 500 } },
  { wait: 4000, call: 'exec_command', args: { cmd: 'echo step3', yield_time_ms: 2000 } },
  { wait: 4000, call: 'exec_command', args: { cmd: 'echo step4', yield_time_ms: 2000 } },
  { wait: 4000, call: 'exec_command', args: { cmd: 'echo step5', yield_time_ms: 2000 } },
  { wait: 4000, text: 'WORK DONE' }
];
let step = -1;
const requests = [];
function lastItem(body) { const items = Array.isArray(body.input) ? body.input : []; return items[items.length - 1] || {}; }
function textOf(item) { return (Array.isArray(item.content) ? item.content : []).map(p => p.text || '').join(''); }

http.createServer((req, res) => {
  let body = '';
  req.on('data', c => { body += c; });
  req.on('end', async () => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname === '/v1/responses' && req.method === 'POST') {
      const parsed = JSON.parse(body);
      const last = lastItem(parsed);
      const userTexts = (parsed.input || []).filter(i => i.type === 'message' && i.role === 'user').map(textOf);
      const isTitle = userTexts.some(t => /^Generate a concise, single-line task title/.test(t));
      requests.push({ at: Date.now(), last, isTitle });
      if (requests.length === 1) fs.writeFileSync(OUT + '/tools.json', JSON.stringify(parsed.tools || [], null, 1));
      say('MODEL #' + requests.length + (userTexts.some(t => /SUBTASK/.test(t)) && !userTexts.some(t => /WORK/.test(t)) ? ' (subagent)' : '') + (isTitle ? ' (title)' : '') + ' last=' + last.type + '/' + (last.role || '') + ' ' + JSON.stringify((textOf(last) || last.output || '').toString().slice(0, 120)));
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      const id = 'r' + requests.length;
      res.write(sse([{ type: 'response.created', response: { id } }]));
      let item;
      const isSub = userTexts.some(t => /SUBTASK/.test(t)) && !userTexts.some(t => /WORK/.test(t));
      if (isSub) item = { type: 'message', role: 'assistant', id: 'm' + id, content: [{ type: 'output_text', text: 'SUB DONE' }] };
      else if (isTitle) item = { type: 'message', role: 'assistant', id: 'm' + id, content: [{ type: 'output_text', text: 'Rig title' }] };
      else {
        const lastUser = userTexts[userTexts.length - 1] || '';
        if (last.type === 'message' && /WORK/.test(lastUser) && step < 0) step = 0;
        else if (step >= 0 && step < steps.length - 1) step++;
        else step = -2;
        const s = step >= 0 ? steps[step] : { text: 'ACK ' + (userTexts.join(' ').match(/MSG-\w+/g) || ['none']).join(',') };
        if (step === steps.length - 1) step = -2;
        if (s.wait) await sleep(s.wait);
        item = s.call
          ? { type: 'function_call', id: 'fc' + id, call_id: 'call_' + id, name: s.call, ...(s.ns ? { namespace: s.ns } : {}), arguments: JSON.stringify(s.args) }
          : { type: 'message', role: 'assistant', id: 'm' + id, content: [{ type: 'output_text', text: s.text }] };
      }
      res.end(sse([
        { type: 'response.output_item.done', item },
        { type: 'response.completed', response: { id, usage: { input_tokens: Number(process.env.TOKENS || 0), input_tokens_details: null, output_tokens: 0, output_tokens_details: null, total_tokens: Number(process.env.TOKENS || 0) } } }
      ]));
      return;
    }
    if (url.pathname === '/internal/sessions/s1/transcript' && req.method === 'PUT') {
      fs.writeFileSync(process.env.STORED_FILE || OUT + '/stored.jsonl', body); res.writeHead(204); res.end(); return;
    }
    if (url.pathname === '/internal/sessions/s1/messages' && req.method === 'GET') {
      res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{"messages":[]}'); return;
    }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end('{}');
  });
}).listen(HUB, '127.0.0.1');

const home = R + '/home';
fs.rmSync(home, { recursive: true, force: true });
fs.mkdirSync(home + '/.codex', { recursive: true });
fs.writeFileSync(home + '/.codex/config.toml', [
  'cli_auth_credentials_store = "file"', 'sandbox_mode = "danger-full-access"', 'approval_policy = "never"', 'model = "gpt-5.4"', 'model_provider = "rig"', ...(process.env.COMPACT ? ['model_auto_compact_token_limit = ' + process.env.COMPACT] : []),
  '[model_providers.rig]', 'name = "rig"', `base_url = "http://127.0.0.1:${HUB}/v1"`,
  'wire_api = "responses"', 'env_key = "CODEX_API_KEY"', 'supports_websockets = false', 'request_max_retries = 0',
  `[projects."${R}/work"]`, 'trust_level = "trusted"', ''
].join('\n'));
const env = {
  PATH: `${S}/clis/node_modules/.bin:/usr/local/bin:/usr/bin:/bin`,
  HOME: home, CODEX_HOME: home + '/.codex', TMPDIR: '/tmp', TERM: 'xterm-256color', LANG: 'C.UTF-8',
  CODEX_API_KEY: 'rig-key',
  AGENTHUB_DRIVER: '/opt/session-agent/codex/driver.js', AGENTHUB_MODE: 'interactive', AGENTHUB_PORT: String(AGENT),
  AGENTHUB_SESSION_ID: 's1', AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${HUB}/internal/sessions/s1`,
  AGENTHUB_CALLBACK_TOKEN: 'rig-token', AGENTHUB_WORKDIR: R + '/work', AGENTHUB_SKILLS_SYNC_SECONDS: '0'
};
const proc = spawn(S + '/proot', ['-b', R + '/rt:/opt/session-agent', '-b', R + '/etc-codex:/etc/codex', '-b', R + '/etc-opencode:/etc/opencode', '-b', fs.mkdtempSync(R + '/tmp-run-') + ':/tmp',
  'node', '/opt/session-agent/server.js'], { env, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
proc.stdout.on('data', d => log.write('[sa] ' + d));
proc.stderr.on('data', d => log.write('[sa!] ' + d));

const term = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
let ws;
const screen = () => { const b = term.buffer.active; const rows = []; for (let i = 0; i < term.rows; i++) rows.push(b.getLine(b.viewportY + i)?.translateToString(true) ?? ''); return rows.join('\n'); };
const dump = name => { fs.writeFileSync(`${OUT}/screen-${name}.txt`, screen()); say('dumped screen-' + name); };
async function until(fn, ms, what) { const end = Date.now() + ms; while (Date.now() < end) { if (fn()) return true; await sleep(100); } say('TIMEOUT waiting for ' + what); return false; }
const sendInput = data => ws.send(JSON.stringify({ type: 'input', data }));
async function paste(text) { sendInput('\x1b[200~' + text + '\x1b[201~'); await sleep(150); sendInput('\r'); }
function connect() {
  ws = new WebSocket(`ws://127.0.0.1:${AGENT}/`);
  ws.on('message', d => term.write(d.toString()));
  return new Promise((r, j) => { ws.once('open', r); ws.once('error', j); });
}

// The hub's follow loop (LiveTranscriptReader): from=next, append whole lines, stop when the
// cursor does not move. Records how far behind the file the cursor is.
const follow = { next: 0, id: null, bytes: '', stuck: 0, polls: 0 };
async function poll() {
  const res = await fetch(`http://127.0.0.1:${AGENT}/agenthub/transcript?from=${follow.next}`, { headers: { 'X-Agent-Token': 'rig-token' } });
  if (!res.ok) return;
  const size = Number(res.headers.get('x-transcript-size')); const next = Number(res.headers.get('x-transcript-next'));
  const id = res.headers.get('x-transcript-id');
  const text = await res.text();
  follow.polls++;
  if (id && id !== follow.id) { if (follow.id) say('POLL transcript id changed ' + follow.id + ' -> ' + id); follow.id = id; if (follow.next) { follow.next = 0; follow.bytes = ''; return; } }
  follow.bytes += text;
  if (next === follow.next && size > next) { follow.stuck++; if (follow.stuck === 1 || follow.stuck % 10 === 0) say(`POLL STUCK at ${next} of ${size} (${follow.stuck}x)`); }
  else follow.stuck = 0;
  follow.next = next;
}
let polling = true;
(async () => { while (polling) { try { await poll(); } catch (e) { say('POLL error ' + e.message); } await sleep(1000); } })();

function userTurns() {
  return follow.bytes.split('\n').filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } })
    .filter(r => r.type === 'response_item' && r.payload?.type === 'message' && r.payload.role === 'user').map(r => textOf(r.payload));
}

async function main() {
  await sleep(1500);
  if (process.env.SERVE) { polling = false; say('serving agent on ' + AGENT); await sleep(Number(process.env.SERVE)); return; }
  await connect();
  term.onData(d => sendInput(d));
  await until(() => /context left|Ask Codex|›/.test(screen()), 60000, 'agent ready');
  await sleep(1500);
  dump('00-ready');
  if (process.env.SCENARIO === 'texts') {
    const variants = {
      plain: 'MSG-T1 plain',
      trailingNewline: 'MSG-T2 trailing newline\n',
      trailingSpaces: 'MSG-T3 trailing spaces   ',
      leadingSpaces: '   MSG-T4 leading spaces',
      tab: 'MSG-T5\ttab inside',
      multiline: 'MSG-T6 line one\n\nline three\n  indented',
      long: 'MSG-T7 ' + 'x'.repeat(3000),
      crlf: 'MSG-T8 a\r\nb',
      unicode: 'MSG-T9 Grüße – “quotes” ✓',
    };
    for (const [name, text] of Object.entries(variants)) {
      const before = requests.length;
      sendInput('\x1b[200~' + text.replace(/\r\n?/g, '\n') + '\x1b[201~'); await sleep(150); sendInput('\r');
      await until(() => requests.length > before, 15000, name);
      await sleep(2500);
    }
    await sleep(2000);
    const turns = userTurns();
    for (const [name, text] of Object.entries(variants)) {
      const sent = text.replace(/\r\n?/g, '\n');
      const tag = sent.match(/MSG-T\d/)[0];
      const got = turns.find(t => t.includes(tag));
      say('TEXT ' + name + ': exact=' + (got === sent) + (got === sent ? '' : ' sent=' + JSON.stringify(sent.slice(0, 80)) + ' recorded=' + JSON.stringify(got?.slice(0, 80)) + ' lens ' + sent.length + '/' + got?.length));
    }
    return;
  }
  await paste('Do the WORK please');
  await until(() => step >= (QUEUE ? 0 : 1), 30000, 'background terminal step');
  await sleep(500);
  await paste('MSG-1 sent while the turn runs');
  await sleep(2500);
  if (process.env.DROP) { say('dropping the websocket'); ws.terminate(); await sleep(2000); await connect(); say('reconnected'); }
  await paste('MSG-2 also sent while the turn runs');
  await until(() => step === -2, 90000, 'turn end');
  await sleep(6000);
  dump('10-after');
  if (process.env.AFTER) {
    await paste('MSG-3 sent after the turn ended');
    await sleep(Number(process.env.AFTER));
    dump('20-after-msg3');
  }
  polling = false;
  await sleep(1200);
  const rollouts = fs.readdirSync(home + '/.codex/sessions', { recursive: true }).filter(f => f.endsWith('.jsonl'));
  for (const f of rollouts) fs.copyFileSync(home + '/.codex/sessions/' + f, OUT + '/rollout-' + path.basename(f));
  fs.writeFileSync(OUT + '/followed.jsonl', follow.bytes);
  const size = rollouts.map(f => fs.statSync(home + '/.codex/sessions/' + f).size);
  say('RESULT rollout sizes=' + size.join(',') + ' followed bytes=' + Buffer.byteLength(follow.bytes) + ' cursor=' + follow.next + ' polls=' + follow.polls + ' stuck=' + follow.stuck);
  say('RESULT user turns seen by the follower: ' + JSON.stringify(userTurns()));
}
main().catch(e => say('ERROR ' + e.stack)).finally(async () => {
  polling = false;
  await sleep(500);
  try { process.kill(-proc.pid, 'SIGTERM'); } catch {}
  await sleep(1500);
  try { process.kill(-proc.pid, 'SIGKILL'); } catch {}
  process.exit(0);
});

// A 2000x2000 PNG of random pixels (about 12 MB): it does not compress, like a large screenshot.
function writeNoisePng(file) {
  const zlib = require('node:zlib'); const crypto = require('node:crypto');
  const W = 2000, H = 2000, raw = Buffer.alloc((W * 3 + 1) * H);
  for (let y = 0; y < H; y++) crypto.randomFillSync(raw, y * (W * 3 + 1) + 1, W * 3);
  const table = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = b => { let r = 0xffffffff; for (const x of b) r = table[(r ^ x) & 255] ^ (r >>> 8); return (r ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => { const l = Buffer.alloc(4); l.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type), data]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([l, td, c]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 2;
  fs.writeFileSync(file, Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]));
}
