// #390 check for Codex and OpenCode: the fleet-delivery-real-cli-rig skill's rig.mjs with a 'busy'
// scenario. Lay out <S>/rig (this file + the skill's build.sh), <S>/clis (npm i @openai/codex@<pin>),
// <S>/proot; R=<S>/rig WT=<worktree>/agent-runtime ./build.sh; SCENARIO=busy node rig.mjs. The mock
// keeps a LONGJOB turn busy for three 10 s model calls; a priority message is pushed during the first.
// AGENT=opencode SCENARIO=busy does the same for OpenCode (npm i opencode-ai@<pin> into <S>/clis): the
// /v1/chat/completions mock answers three todowrite tool calls, each after 10 s.
// #173 rig: real pinned Codex TUI in the real common/server.js PTY (under proot, so the branch's
// requirements.toml is /etc/codex and the branch runtime is /opt/session-agent), a stub hub
// with a real inbox (take = mark delivered), and a mock Responses model that records requests.
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
const CLAUDE = process.env.AGENT === 'claude';
const OPENCODE = process.env.AGENT === 'opencode';
const AGENT = HUB + 1000;
const log = fs.createWriteStream(R + '/rig.log');
const t0 = Date.now();
const say = (...a) => { const line = `[${((Date.now() - t0) / 1000).toFixed(1)}s] ` + a.join(' '); console.log(line); log.write(line + '\n'); };

// ---- stub hub + model --------------------------------------------------------------------
const inbox = [];          // undelivered rows
const taken = [];          // { id, via, at }
const modelRequests = [];  // { at, last }
let slowMs = Number(process.env.SLOW_MS || 8000);
let lastDone = 0;
const busyReqs = []; let busyEnd = 0; // when the last non-title model answer finished

function lastText(body) {
  const items = Array.isArray(body.input) ? body.input : [];
  for (let i = items.length - 1; i >= 0; i--) {
    const item = items[i];
    if (item.type === 'message' && (item.role === 'user' || item.role === 'developer')) {
      const parts = Array.isArray(item.content) ? item.content : [];
      return { role: item.role, text: parts.map(p => p.text || '').join('') };
    }
  }
  return { role: '?', text: '' };
}
const sse = events => events.map(e => `event: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`).join('');

http.createServer((req, res) => {
  let body = '';
  req.on('data', c => { body += c; });
  req.on('end', async () => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname === '/v1/chat/completions') {
      const json = JSON.parse(body || '{}');
      const msgs = json.messages || [];
      const textOf = m => typeof m.content === 'string' ? m.content : (m.content || []).map(c => c.text || '').join('');
      const lastUser = [...msgs].reverse().find(m => m.role === 'user');
      const text = lastUser ? textOf(lastUser) : '';
      const system = msgs.filter(m => m.role === 'system').map(textOf).join(' ');
      const isTitle = !json.stream || /title/i.test(system.slice(0, 400));
      if (!isTitle && body.includes('LONGJOB')) {
        const has = body.includes('MSG-B');
        const outs = msgs.filter(m => m.role === 'tool').length;
        busyReqs.push({ at: Date.now(), has, outs });
        fs.writeFileSync(R + '/busy-req-' + busyReqs.length + '.json', body);
        say('BUSY main request #' + busyReqs.length + ' has MSG-B=' + has + ' tool_results=' + outs + (busyReqs.length === 1 ? ' tools=' + (json.tools || []).map(t => t.function && t.function.name).join(',') : ''));
        res.writeHead(200, { 'content-type': 'text/event-stream' });
        const b = { id: 'c' + busyReqs.length, object: 'chat.completion.chunk', created: 1, model: 'm1' };
        const send = o => res.write('data: ' + JSON.stringify(o) + '\n\n');
        send({ ...b, choices: [{ index: 0, delta: { role: 'assistant', content: '' } }] });
        const n = busyReqs.length;
        if (n <= 3) {
          await new Promise(r => setTimeout(r, 10000));
          send({ ...b, choices: [{ index: 0, delta: { tool_calls: [{ index: 0, id: 'call_' + n, type: 'function', function: { name: 'todowrite', arguments: JSON.stringify({ todos: [{ id: 's' + n, content: 'step ' + n, status: 'in_progress', priority: 'medium' }] }) } }] } }] });
          send({ ...b, choices: [{ index: 0, delta: {}, finish_reason: 'tool_calls' }] });
          res.end('data: [DONE]\n\n');
          return;
        }
        send({ ...b, choices: [{ index: 0, delta: { content: 'LONGJOB done' + (has ? ' ACK MSG-B' : '') } }] });
        send({ ...b, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] });
        res.end('data: [DONE]\n\n');
        if (!busyEnd) { busyEnd = Date.now(); say('BUSY turn final answer sent (turn ends)'); }
        lastDone = Date.now();
        return;
      }
      modelRequests.push({ at: Date.now(), last: { role: 'user', text }, isTitle });
      say('MODEL request #' + modelRequests.length + (isTitle ? ' (title)' : '') + ' last user: ' + JSON.stringify(text.slice(-300)));
      if (!json.stream) { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify({ id: 'x', object: 'chat.completion', created: 1, model: 'm1', choices: [{ index: 0, message: { role: 'assistant', content: 'Rig title' }, finish_reason: 'stop' }] })); return; }
      res.writeHead(200, { 'content-type': 'text/event-stream' });
      const b = { id: 'c', object: 'chat.completion.chunk', created: 1, model: 'm1' };
      const send = o => res.write('data: ' + JSON.stringify(o) + '\n\n');
      const slow = !isTitle && ((/SLOW/.test(text) && !/AgentHub/.test(text)) || /CHAIN/.test(text));
      send({ ...b, choices: [{ index: 0, delta: { role: 'assistant', content: '' } }] });
      if (slow) await new Promise(r => setTimeout(r, /CHAIN/.test(text) ? 3000 : slowMs));
      send({ ...b, choices: [{ index: 0, delta: { content: isTitle ? 'Rig title' : 'ACK ' + (text.match(/MSG-\w+/g) || ['none']).join(',') } }] });
      send({ ...b, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] });
      res.end('data: [DONE]\n\n');
      if (!isTitle) lastDone = Date.now();
      return;
    }
    if (url.pathname.startsWith('/v1/messages')) {
      const json = JSON.parse(body || '{}');
      if (url.pathname.includes('count_tokens') || !json.stream) { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify({ input_tokens: 10, id: 'x', type: 'message', role: 'assistant', model: 'm', content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } })); return; }
      const msgs = json.messages || [];
      const lastUser = [...msgs].reverse().find(m => m.role === 'user');
      const text = lastUser ? (typeof lastUser.content === 'string' ? lastUser.content : lastUser.content.map(c => c.text || '').join('')) : '';
      const isTitle = /suggestion|title/i.test(JSON.stringify(json.system || '')) && !/SLOW/.test(text) ;
      modelRequests.push({ at: Date.now(), last: { role: 'user', text }, isTitle: /Reply with ONLY the suggestion/.test(text) });
      say('MODEL request #' + modelRequests.length + ' last user: ' + JSON.stringify(text.slice(-300)));
      const slow = /SLOW/.test(text) && !/AgentHub/.test(text);
      res.writeHead(200, { 'content-type': 'text/event-stream' });
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n');
      const msg = { id: 'msg_' + modelRequests.length, type: 'message', role: 'assistant', model: json.model || 'm', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 1 } };
      ev('message_start', { message: msg });
      if (slow) await new Promise(r => setTimeout(r, slowMs));
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'ACK ' + (text.match(/MSG-\w+/g) || ['none']).join(',') } });
      ev('content_block_stop', { index: 0 });
      ev('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } });
      ev('message_stop', {});
      res.end();
      return;
    }
    if (url.pathname === '/v1/responses' && req.method === 'POST') {
      const parsed = JSON.parse(body);
      const last = lastText(parsed);
      const isTitle = /^Generate a concise, single-line task title/.test(last.text);
      modelRequests.push({ at: Date.now(), last, isTitle });
      say('MODEL request #' + modelRequests.length + (isTitle ? ' (title helper)' : '') + ' last ' + last.role + ': ' + JSON.stringify(last.text.slice(0, 400)));
      fs.appendFileSync(R + '/model.log', JSON.stringify({ n: modelRequests.length, last, isTitle }) + '\n');
      if (!isTitle && body.includes('LONGJOB')) {
        const has = body.includes('MSG-B');
        const tools = (parsed.tools || []).map(t => t.name || t.type);
        const outs = (parsed.input || []).filter(i => i.type === 'function_call_output').length;
        busyReqs.push({ at: Date.now(), has, outs });
        fs.writeFileSync(R + '/busy-req-' + busyReqs.length + '.json', body);
        say('BUSY main request #' + busyReqs.length + ' has MSG-B=' + has + ' function_call_outputs=' + outs + (busyReqs.length === 1 ? ' tools=' + tools.join(',') : ''));
        res.writeHead(200, { 'Content-Type': 'text/event-stream' });
        res.write(sse([{ type: 'response.created', response: { id: 'r' + modelRequests.length } }]));
        const n = busyReqs.length;
        if (n <= 3) {
          await new Promise(r => setTimeout(r, 10000));
          res.end(sse([
            { type: 'response.output_item.done', item: { type: 'function_call', id: 'fc' + n, call_id: 'call_' + n, name: 'update_plan', arguments: JSON.stringify({ plan: [{ step: 'step ' + n, status: 'in_progress' }] }) } },
            { type: 'response.completed', response: { id: 'r' + modelRequests.length, usage: { input_tokens: 0, input_tokens_details: null, output_tokens: 0, output_tokens_details: null, total_tokens: 0 } } }
          ]));
          return;
        }
        const answer = 'LONGJOB done' + (has ? ' ACK MSG-B' : '');
        res.end(sse([
          { type: 'response.output_item.done', item: { type: 'message', role: 'assistant', id: 'm' + modelRequests.length, content: [{ type: 'output_text', text: answer }] } },
          { type: 'response.completed', response: { id: 'r' + modelRequests.length, usage: { input_tokens: 0, input_tokens_details: null, output_tokens: 0, output_tokens_details: null, total_tokens: 0 } } }
        ]));
        if (!busyEnd) { busyEnd = Date.now(); say('BUSY turn final answer sent (turn ends)'); }
        lastDone = Date.now();
        return;
      }
      const slow = !isTitle && ((/SLOW/.test(last.text) && !/AgentHub/.test(last.text)) || /CHAIN/.test(last.text));
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      res.write(sse([{ type: 'response.created', response: { id: 'r' + modelRequests.length } }]));
      if (slow) await new Promise(r => setTimeout(r, /CHAIN/.test(last.text) ? 3000 : slowMs));
      const answer = isTitle ? 'Rig title' : 'ACK ' + (last.text.match(/MSG-\w+/g) || ['none']).join(',');
      res.end(sse([
        { type: 'response.output_item.done', item: { type: 'message', role: 'assistant', id: 'm' + modelRequests.length, content: [{ type: 'output_text', text: answer }] } },
        { type: 'response.completed', response: { id: 'r' + modelRequests.length, usage: { input_tokens: 0, input_tokens_details: null, output_tokens: 0, output_tokens_details: null, total_tokens: 0 } } }
      ]));
      if (!isTitle) lastDone = Date.now();
      return;
    }
    if (url.pathname === '/internal/sessions/s1/messages' && req.method === 'GET') {
      if (req.headers['x-agent-token'] !== 'rig-token') { res.writeHead(401); res.end(); return; }
      const via = url.searchParams.get('via') === 'next_turn' ? 'next_turn' : 'inbox';
      const batch = inbox.splice(0, 4);
      for (const m of batch) taken.push({ id: m.id, via, at: Date.now(), afterDone: Date.now() - lastDone });
      if (batch.length) say('HUB take via=' + via + ': ' + batch.map(m => m.id).join(','));
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ messages: batch.map(m => ({ id: m.id, from: m.from, fromTitle: m.fromTitle, body: m.body, priority: m.priority })) }));
      return;
    }
    if (process.env.HUB_VERBOSE) say('HUB ' + req.method + ' ' + url.pathname);
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end('{}');
  });
}).listen(HUB, '127.0.0.1');

// ---- session agent under proot -------------------------------------------------------------
const home = R + '/home';
fs.writeFileSync(home + '/.codex/config.toml', [
  'cli_auth_credentials_store = "file"',
  'model = "gpt-5.4"',
  'model_provider = "rig"',
  `[model_providers.rig]`, 'name = "rig"', `base_url = "http://127.0.0.1:${HUB}/v1"`,
  'wire_api = "responses"', 'env_key = "CODEX_API_KEY"', 'supports_websockets = false', 'request_max_retries = 0',
  `[projects."${R}/work"]`, 'trust_level = "trusted"', ''
].join('\n'));
if (CLAUDE) {
  fs.mkdirSync(home + '/.claude', { recursive: true });
  const key = 'sk-ant-rig-0000000000000000000000000000';
  fs.writeFileSync(home + '/.claude.json', JSON.stringify({ hasCompletedOnboarding: true, customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
    projects: { [R + '/work']: { hasTrustDialogAccepted: true } } }));
  for (const f of ['register.js']) {
    const p = R + '/rt/claude/mods/agenthub-fleet/hooks/' + f;
    fs.writeFileSync(p, fs.readFileSync(p, 'utf8').replaceAll('127.0.0.1:7681', '127.0.0.1:' + AGENT));
  }
}
const env = {
  PATH: `${S}/clis/node_modules/.bin:/usr/local/bin:/usr/bin:/bin`,
  HOME: home, CODEX_HOME: home + '/.codex', TMPDIR: '/tmp', TERM: 'xterm-256color', LANG: 'C.UTF-8',
  CODEX_API_KEY: 'rig-key',
  AGENTHUB_DRIVER: '/opt/session-agent/codex/driver.js', AGENTHUB_MODE: 'interactive', AGENTHUB_PORT: String(AGENT),
  AGENTHUB_SESSION_ID: 's1', AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${HUB}/internal/sessions/s1`,
  AGENTHUB_CALLBACK_TOKEN: 'rig-token', PROTO_PASTE: process.env.PROTO_PASTE || '', PROTO_ESC_GAP: process.env.PROTO_ESC_GAP || '', AGENTHUB_WORKDIR: R + '/work', AGENTHUB_SKILLS_SYNC_SECONDS: '0'
};
if (OPENCODE) {
  fs.mkdirSync(home + '/.config/opencode', { recursive: true });
  fs.writeFileSync(home + '/.config/opencode/opencode.json', JSON.stringify({ model: 'mock/m1', provider: { mock: { npm: '@ai-sdk/openai-compatible', name: 'Mock',
    options: { baseURL: 'http://127.0.0.1:' + HUB + '/v1', apiKey: 'x' }, models: { m1: { name: 'M1', tool_call: true } } } } }));
  Object.assign(env, { AGENTHUB_DRIVER: '/opt/session-agent/opencode/driver.js', OPENCODE_DISABLE_AUTOUPDATE: '1', OPENCODE_DISABLE_SHARE: '1' });
}
if (CLAUDE) Object.assign(env, {
  AGENTHUB_DRIVER: '/opt/session-agent/claude/driver.js', CLAUDE_CODE_PLUGIN_DIRS: process.env.NO_MOD ? '' : '/opt/session-agent/claude/mods/agenthub-fleet',
  AGENTHUB_MOD_TOKEN: 'rig-mod-token', ANTHROPIC_API_KEY: 'sk-ant-rig-0000000000000000000000000000', ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + HUB,
  PATH: (process.env.CLAUDE_DIR ? process.env.CLAUDE_DIR + ':' : '') + '/usr/local/bin:/usr/bin:/bin'
});
// #340: a settings UserPromptSubmit hook that blocks the first DROPS prompts, so Claude Code
// answers the mod's $.prompt.submit with { drop }.
if (CLAUDE && process.env.DROPS) {
  const counter = home + '/drops';
  fs.writeFileSync(home + '/block-hook.sh', `#!/bin/sh
cat > /dev/null
n=$(cat ${counter} 2>/dev/null || echo 0)
echo "hook call, blocked so far $n" >> ${home}/hook-calls.log
if [ "$n" -lt ${Number(process.env.DROPS)} ]; then echo $((n+1)) > ${counter}; echo "blocked by the #340 proof hook ($((n+1)))" >&2; exit 2; fi
exit 0
`, { mode: 0o755 });
  fs.writeFileSync(home + '/.claude/settings.json', JSON.stringify({ hooks: { UserPromptSubmit: [{ hooks: [{ type: 'command', command: home + '/block-hook.sh' }] }] } }));
}
const proc = spawn(S + '/proot', ['-b', R + '/rt:/opt/session-agent', '-b', R + '/etc-codex:/etc/codex', '-b', R + '/etc-opencode:/etc/opencode', '-b', fs.mkdtempSync(R + '/tmp-run-') + ':/tmp',
  'node', '/opt/session-agent/server.js'], { env, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
proc.stdout.on('data', d => log.write('[sa] ' + d));
proc.stderr.on('data', d => log.write('[sa!] ' + d));

// ---- the "browser": a websocket client rendering with xterm, answering terminal queries -------
const term = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
let ws;
const screen = () => { const b = term.buffer.active; const rows = []; for (let i = 0; i < term.rows; i++) rows.push(b.getLine(b.viewportY + i)?.translateToString(true) ?? ''); return rows.join('\n'); };
const dump = name => { fs.writeFileSync(`${R}/screen-${name}.txt`, screen()); say('dumped screen-' + name); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, ms, what) { const end = Date.now() + ms; while (Date.now() < end) { if (fn()) return true; await sleep(100); } say('TIMEOUT waiting for ' + what); return false; }
const sendInput = data => ws.send(JSON.stringify({ type: 'input', data }));
async function paste(text) { sendInput('\x1b[200~' + text + '\x1b[201~'); await sleep(150); sendInput('\r'); }

async function push(id, body, extra = {}) {
  const m = { id, from: 'peer-7', fromTitle: 'Reviewer Bot', body, priority: false, ...extra };
  inbox.push(m);
  const res = await fetch(`http://127.0.0.1:${AGENT}/agenthub/messages`, { method: 'POST', headers: { 'X-Agent-Token': 'rig-token', 'Content-Type': 'application/json' }, body: JSON.stringify(m) });
  const answer = await res.json();
  // What the backend does with that answer (SessionMessageDelivery): pty/chat/mod mark it delivered.
  if (['pty', 'chat', 'queued-for-mod'].includes(answer.delivered)) { const i = inbox.indexOf(m); if (i >= 0) inbox.splice(i, 1); taken.push({ id, via: answer.delivered, at: Date.now() }); }
  say('SEND ' + id + (extra.priority ? ' (priority)' : '') + ' -> ' + JSON.stringify(answer));
  return answer;
}
const modelSaw = id => modelRequests.find(r => !r.isTitle && r.last.text.includes(id));

async function main() {
  await sleep(1500);
  ws = new WebSocket(`ws://127.0.0.1:${AGENT}/`);
  await new Promise((r, j) => { ws.once('open', r); ws.once('error', j); });
  let raw='';ws.on('message', d => { raw += d.toString(); term.write(d.toString()); });globalThis.rawOut=()=>raw;
  term.onData(d => sendInput(d)); // terminal query replies, as the browser's xterm sends them
  const scenario = process.env.SCENARIO || 'all';
  await until(() => (CLAUDE ? /for shortcuts|❯|auto mode on/ : OPENCODE ? /Ask anything/ : /context left|Ask Codex|›/).test(screen()), OPENCODE ? 150000 : 60000, 'agent ready');
  await sleep(1500);
  dump('00-ready');

  if (scenario === 'busy') {
    await paste('Please do the LONGJOB task.');
    await until(() => busyReqs.length >= 1, 30000, 'busy start');
    await sleep(3000);
    const sentAt = Date.now();
    const answer = await push('MSG-B', 'MSG-B: priority status request.', { priority: true });
    await until(() => busyEnd && busyReqs.some(r => r.has), 120000, 'MSG-B seen');
    await sleep(3000);
    dump('busy');
    const first = busyReqs.findIndex(r => r.has);
    say('BUSY RESULT: answer=' + JSON.stringify(answer) + ' sentAt=' + ((sentAt - t0) / 1000).toFixed(1) + 's firstCarrier=' + (first < 0 ? 'never' : '#' + (first + 1) + ' at +' + ((busyReqs[first].at - sentAt) / 1000).toFixed(1) + 's') +
      ' turnEnd=' + (busyEnd ? '+' + ((busyEnd - sentAt) / 1000).toFixed(1) + 's' : 'none') + ' reqs=' + JSON.stringify(busyReqs.map(r => ({ t: +((r.at - sentAt) / 1000).toFixed(1), has: r.has, outs: r.outs }))) +
      ' hubInboxLeft=[' + inbox.map(m => m.id).join(',') + '] takes=' + JSON.stringify(taken.map(x => x.id + ':' + x.via)));
    return;
  }
  if (scenario === 'claudedrop') {
    const ver = (await new Promise(r => { let o = ''; const p = spawn('claude', ['--version'], { env }); p.stdout.on('data', d => o += d); p.on('close', () => r(o.trim())); }));
    say('claude on the rig PATH: ' + ver);
    const idle = await push('MSG-D', 'MSG-D: idle note whose first submit is dropped.');
    await until(() => modelSaw('MSG-D'), 30000, 'MSG-D');
    await sleep(3000);
    dump('claudedrop');
    const calls = fs.existsSync(home + '/hook-calls.log') ? fs.readFileSync(home + '/hook-calls.log', 'utf8').trim().split('\n').length : 0;
    say('CLAUDEDROP RESULT: answer=' + idle.delivered + ' model saw MSG-D=' + Boolean(modelSaw('MSG-D')) + ' times=' + modelRequests.filter(r => !r.isTitle && r.last.text.includes('MSG-D')).length +
      ' UserPromptSubmit calls=' + calls + ' hub inbox left=' + inbox.map(m => m.id).join(',') + ' takes=' + JSON.stringify(taken.map(x => x.id + ':' + x.via)));
    return;
  }
  if (scenario === 'claude') {
    const idle = await push('MSG-I', 'MSG-I: idle note.');
    await until(() => modelSaw('MSG-I'), 30000, 'MSG-I');
    await sleep(3000);
    await paste('Please do the SLOW task');
    await until(() => modelRequests.some(r => /SLOW/.test(r.last.text)), 30000, 'slow');
    await sleep(1000);
    const busy = await push('MSG-W', 'MSG-W: sent while working.');
    await until(() => modelSaw('MSG-W'), slowMs + 30000, 'MSG-W');
    await sleep(3000);
    const prio = await push('MSG-P', 'MSG-P: priority.', { priority: true });
    await until(() => modelSaw('MSG-P'), 30000, 'MSG-P');
    await sleep(2000);
    dump('claude');
    say('CLAUDE RESULT: answers idle=' + idle.delivered + ' busy=' + busy.delivered + ' priority=' + prio.delivered +
      '; model saw ' + ['MSG-I', 'MSG-W', 'MSG-P'].map(id => id + '=' + Boolean(modelSaw(id))).join(' ') + '; hub inbox left=' + inbox.map(m => m.id).join(',') + ' takes=' + JSON.stringify(taken.map(x => x.id + ':' + x.via)));
    return;
  }
  if (scenario === 'heldkeys') {
    await paste('hello');
    await until(() => modelSaw('hello') || modelRequests.some(r => r.last.text.includes('hello')), 20000, 'hello');
    await sleep(3000);
    const h = await push('MSG-H', 'MSG-H: typed while the person types.');
    // As soon as the typed text shows in the prompt box, and before its Enter, the person types.
    await until(() => /MSG-H: typed/.test(screen()), 5000, 'MSG-H in box');
    for (const ch of 'zz') { sendInput(ch); await sleep(20); }
    await until(() => modelSaw('MSG-H'), 10000, 'MSG-H');
    await sleep(3000);
    await push('MSG-H2', 'MSG-H2: must not join the draft.');
    await sleep(6000);
    dump('heldkeys');
    const draftHasH2 = modelRequests.some(r => /zz/.test(r.last.text) && r.last.text.includes('MSG-H2'));
    say('HELD RESULT: MSG-H answer=' + h.delivered + ' model saw MSG-H=' + Boolean(modelSaw('MSG-H')) + '; MSG-H2 seen while draft in box=' + Boolean(modelSaw('MSG-H2')) + ' inbox=' + inbox.map(m => m.id) + ' draftHasH2=' + draftHasH2 + ' box=' + JSON.stringify((screen().match(/.*zz.*/) || [''])[0].trim()));
    sendInput('\r');
    await until(() => modelSaw('MSG-H2'), 20000, 'MSG-H2');
    say('HELD RESULT after submit: MSG-H2 seen=' + Boolean(modelSaw('MSG-H2')) + ' joined draft=' + modelRequests.some(r => /zz/.test(r.last.text) && r.last.text.includes('MSG-H2')));
    return;
  }
  if (scenario === 'abort') {
    slowMs = 30000;
    await paste('Please do the SLOW task');
    await until(() => modelSaw('SLOW'), 20000, 'slow');
    await sleep(1000);
    await push('MSG-X', 'MSG-X: arrived before the abort.');
    sendInput('\x1b'); await sleep(300); sendInput('\x1b');
    await sleep(6000);
    dump('abort');
    say('ABORT RESULT (after Esc): model saw MSG-X=' + Boolean(modelSaw('MSG-X')) + ' inbox=' + inbox.map(m => m.id));
    slowMs = 8000;
    await paste('carry on');
    await until(() => modelSaw('MSG-X'), 30000, 'MSG-X');
    const t = taken.find(x => x.id === 'MSG-X');
    say('ABORT RESULT (next turn): model saw MSG-X=' + Boolean(modelSaw('MSG-X')) + ' after "carry on"=' + (modelRequests.findIndex(r => r.last.text.includes('carry on')) < modelRequests.findIndex(r => !r.isTitle && r.last.text.includes('MSG-X'))) + ' afterDone=' + (t && t.afterDone));
    return;
  }
  if (scenario === 'chain') {
    await paste('Please do the SLOW task');
    await until(() => modelSaw('SLOW'), 20000, 'slow');
    await sleep(1000);
    for (let i = 1; i <= 5; i++) {
      await push('MSG-' + i, 'MSG-' + i + ' CHAIN step ' + i);
      await until(() => modelSaw('MSG-' + i), 30000, 'MSG-' + i);
      await sleep(500);
    }
    await sleep(5000);
    say('CHAIN RESULT: ' + [1,2,3,4,5].map(i => { const t = taken.find(x => x.id === 'MSG-' + i); return 'MSG-' + i + '=' + (modelSaw('MSG-' + i) && t ? (t.afterDone < 1200 ? 'stop-hook' : 'typed-idle') + '(' + t.afterDone + 'ms after turn end)' : 'never'); }).join(' ') + ' taken=' + JSON.stringify(taken.map(x => x.id + ':' + x.via)));
    dump('chain');
    return;
  }
  if (scenario === 'sizes') {
    // #219: messages typed into an idle agent right after a turn ended: short, multi-line, long.
    const line = i => 'line ' + i + ' of a long peer report, padded with words so it wraps across the composer width.';
    const cases = (process.env.CASES || 'short,multi,long4k,long8k').split(',').map(k => {
      if (k === 'short') return [k, 'MSG-S: short note.'];
      if (k === 'multi') return [k, 'MSG-ML: first line\nsecond line\n\n- bullet one\n- bullet two\nlast line'];
      const n = Number(k.replace(/\D/g, '')) * 1024;
      let body = 'MSG-L' + (n / 1024) + 'K: start\n'; let i = 0;
      while (body.length < n) body += line(i++) + '\n';
      return [k, body + 'END-L' + (n / 1024) + 'K'];
    });
    const results = [];
    for (const [k, body] of cases) {
      const id = body.match(/MSG-\w+/)[0];
      const t = Date.now();
      await paste('quick turn before ' + k);
      await until(() => lastDone >= t, 20000, 'turn end ' + k);
      if (process.env.PUSH_DELAY) await sleep(Number(process.env.PUSH_DELAY));
      const answer = await push(id, body);
      const sent = Date.now();
      const ok = await until(() => modelSaw(id), Number(process.env.WAIT_MS || 25000), id + ' at model');
      const req = modelSaw(id);
      await sleep(2500);
      dump('sizes-' + k);
      const left = /MSG-\w+|END-L/.test(screen());
      const full = req ? req.last.text.includes(body.split('\n').pop()) : false;
      results.push(k + '(' + body.length + 'B): seenBy=' + modelRequests.filter(r => !r.isTitle && r.last.text.includes(id)).length + ' ' + (ok ? 'submitted ' + ((req.at - sent) / 1000).toFixed(1) + 's, full=' + full : 'NOT SUBMITTED') + ' screenShowsMsg=' + left + ' inbox=' + inbox.map(m => m.id));
      say('SIZES ' + results[results.length - 1] + ' answer=' + answer.delivered);
      if (!ok) { sendInput('\x15'); await sleep(500); inbox.length = 0; }
      await sleep(1500);
    }
    say('SIZES RESULT: ' + results.join(' | '));
    return;
  }
  if (scenario === 'intidle') {
    // #224: an interrupting priority message to an agent that is idle: the Escapes hit an empty prompt.
    await sleep(2000);
    const sent = Date.now();
    const answer = await push('MSG-II', 'MSG-II: interrupt while idle.', { priority: true, interrupt: true });
    const ok = await until(() => modelSaw('MSG-II'), 15000, 'MSG-II');
    await sleep(1500);
    dump('intidle');
    await paste('follow-up MSG-FU');
    const fu = await until(() => modelSaw('MSG-FU'), 15000, 'MSG-FU');
    say('INTIDLE RESULT: answer=' + answer.delivered + ' model saw=' + ok + (ok ? ' after ' + (modelSaw('MSG-II').at - sent) + 'ms count=' + modelRequests.filter(r => !r.isTitle && r.last.text.includes('MSG-II')).length : '') + ' follow-up works=' + fu);
    return;
  }
  if (scenario === 'prio3') {
    // #222: priority messages, idle / mid-turn / mid-turn with interrupt.
    const big = (id, n) => { let b = id + ' start\n', i = 0; while (b.length < n) b += 'line ' + i++ + ' of a long priority note, padded so it wraps.\n'; return b + 'END-' + id; };
    const n = Number(process.env.PRIO_BYTES || 3900);
    const out = [];
    const one = async (id, extra) => {
      const sent = Date.now();
      const answer = await push(id, big(id, n), { priority: true, ...extra });
      const answered = Date.now() - sent;
      const ok = await until(() => modelSaw(id), Number(process.env.WAIT_MS || 30000), id);
      const req = modelSaw(id);
      dump('prio3-' + id);
      out.push(id + ': answer=' + answer.delivered + ' in ' + answered + 'ms, model saw=' + ok + (req ? ' after ' + (req.at - sent) + 'ms full=' + req.last.text.includes('END-' + id) + ' count=' + modelRequests.filter(r => !r.isTitle && r.last.text.includes(id)).length : '') + ' sentAbs=' + sent);
      say('PRIO3 ' + out[out.length - 1]);
    };
    await one('MSG-PI', {});
    await sleep(3000);
    await paste('Please do the SLOW task');
    await until(() => modelSaw('SLOW'), 20000, 'slow');
    await sleep(1000);
    await one('MSG-PB', {});
    await sleep(4000);
    await paste('Please do the SLOW task again');
    await until(() => modelRequests.some(r => r.last.text.includes('SLOW task again')), 20000, 'slow2');
    await sleep(1000);
    await one('MSG-PX', { interrupt: true });
    await sleep(3000);
    say('PRIO3 RESULT: ' + out.join(' | '));
    return;
  }
  if (scenario === 'prio') {
    let body = 'MSG-PRIO start\n', i = 0;
    while (body.length < Number(process.env.PRIO_BYTES || 3900)) body += 'line ' + i++ + ' of a long priority note, padded so it wraps.\n';
    const answer = await push('MSG-PRIO', body + 'END', { priority: true });
    const ok = await until(() => modelSaw('MSG-PRIO'), 15000, 'MSG-PRIO');
    dump('prio');
    say('PRIO RESULT: ' + body.length + 'B answer=' + answer.delivered + ' submitted=' + ok);
    return;
  }
  if (scenario === 'ingest') {
    // #219: how long the TUI takes to show a burst it was typed, and from when an Enter submits it.
    const sizes = (process.env.SIZES || '256,1024,2048,4096,8192').split(',').map(Number);
    const line = i => 'line ' + i + ' of a long peer report, padded with words so it wraps across the composer width.';
    const make = n => { let b = 'MSG-P' + n + ' start\n', i = 0; while (b.length < n) b += line(i++) + '\n'; return b + 'END-P' + n; };
    const tail = s => screen().includes(s);
    for (const n of sizes) {
      const text = make(n);
      const t = Date.now();
      sendInput(text);
      let shownAt = 0, quietAt = 0, lastLen = 0, lastRaw = Date.now();
      while (Date.now() - t < 60000) {
        if (!shownAt && tail('END-P' + n)) shownAt = Date.now() - t;
        const len = globalThis.rawOut().length;
        if (len !== lastLen) { lastLen = len; lastRaw = Date.now(); }
        if (shownAt && Date.now() - lastRaw > 1000) { quietAt = lastRaw - t; break; }
        await sleep(20);
      }
      dump('ingest-' + n);
      const enterAt = Date.now() - t;
      sendInput('\r');
      const ok = await until(() => modelSaw('MSG-P' + n), 8000, 'P' + n);
      say('INGEST ' + n + 'B: end shown after ' + shownAt + 'ms, output quiet after ' + quietAt + 'ms, Enter at ' + enterAt + 'ms -> ' + (ok ? 'submitted' : 'NOT submitted'));
      if (!ok) { dump('ingest-' + n + '-stuck'); sendInput('\x03'); await sleep(1000); }
      await sleep(2500);
    }
    return;
  }
  if (scenario === 'variants') {
    const sizes = (process.env.SIZES || '2048,8192').split(',').map(Number);
    const variants = (process.env.VARIANTS || 'raw,bracketed,chunked,twoenter').split(',');
    const line = i => 'line ' + i + ' of a long peer report, padded with words so it wraps across the composer width.';
    const make = (id, n) => { let b = id + ' start\n', i = 0; while (b.length < n) b += line(i++) + '\n'; return b + 'END-' + id; };
    const out = [];
    for (const n of sizes) for (const v of variants) {
      const id = 'MSG-V' + v + n;
      const text = make(id, n);
      const t = Date.now();
      if (v === 'bracketed') sendInput('\x1b[200~' + text + '\x1b[201~');
      else if (v === 'chunked') { for (let i = 0; i < text.length; i += 512) { sendInput(text.slice(i, i + 512)); await sleep(30); } }
      else sendInput(text);
      await sleep(300);
      dump('before-enter-' + v + n);
      const shownBefore = screen().includes('END-' + id) || /Pasted Content/.test(screen());
      sendInput('\r');
      if (v === 'twoenter') { await sleep(1000); if (!modelSaw(id)) sendInput('\r'); }
      const ok = await until(() => modelSaw(id), Number(process.env.WAIT_MS || 6000), id);
      const req = modelSaw(id);
      out.push(n + 'B ' + v + '=' + (ok ? 'ok full=' + req.last.text.includes('END-' + id) + ' trailingNL=' + /\n$/.test(req.last.text) : 'STUCK') + ' endShownBeforeEnter=' + shownBefore);
      say('VARIANT ' + out[out.length - 1]);
      if (!ok) { dump('variant-' + v + n); sendInput('\x03'); await sleep(1500); }
      await sleep(2500);
    }
    fs.writeFileSync(R + '/variants-raw.bin', globalThis.rawOut());
    say('VARIANTS RESULT: ' + out.join(' | '));
    return;
  }
  if (scenario === 'enterdelay') {
    // #219: Enter a fixed time after the burst, as terminal-inject does: which delay works per size.
    const sizes = (process.env.SIZES || '512,1024,2048,4096').split(',').map(Number);
    const delays = (process.env.DELAYS || '300,1000,2000').split(',').map(Number);
    const line = i => 'line ' + i + ' of a long peer report, padded with words so it wraps across the composer width.';
    const make = (n, d) => { let b = 'MSG-E' + n + 'x' + d + ' start\n', i = 0; while (b.length < n) b += line(i++) + '\n'; return b + 'END'; };
    const out = [];
    for (const n of sizes) for (const d of delays) {
      const id = 'MSG-E' + n + 'x' + d;
      sendInput(make(n, d));
      await sleep(d);
      sendInput('\r');
      const ok = await until(() => modelSaw(id), 6000, id);
      out.push(n + 'B@' + d + 'ms=' + (ok ? 'ok' : 'STUCK'));
      say('ENTERDELAY ' + out[out.length - 1]);
      if (!ok) { dump('enterdelay-' + n + 'x' + d); sendInput('\x03'); await sleep(1500); dump('enterdelay-' + n + 'x' + d + '-cleared'); }
      await sleep(2500);
    }
    say('ENTERDELAY RESULT: ' + out.join(' '));
    return;
  }
  if (scenario === 'warn') {
    await paste('Please do the SLOW task');
    await sleep(2000);
    for (const k of ['\x1bOQ']) { sendInput(k); await sleep(1500); dump('w1'); }
    sendInput('\x1b[C'); await sleep(800); dump('w2');
    return;
  }
  if (scenario === 'menu') {
    fs.writeFileSync(R + '/idle-raw.bin', globalThis.rawOut());
    sendInput('\x1bOQ'); await sleep(1500); dump('f2'); sendInput('\x1b'); await sleep(800);
    await paste(process.env.MENU_CMD || '/model');
    await sleep(2500);
    dump('menu');
    fs.writeFileSync(R + '/menu-raw.bin', globalThis.rawOut());
    const b = await push('MSG-M', 'MSG-M: during a menu.');
    await sleep(5000);
    dump('menu-after-send');
    say('M RESULT: model saw MSG-M=' + Boolean(modelSaw('MSG-M')) + ' inbox=' + inbox.map(m=>m.id));
    return;
  }

  // A: message while the agent works -> the turn's next input via the Stop hook
  await paste('Please do the SLOW task');
  await until(() => modelRequests.some(r => /SLOW/.test(r.last.text)), 20000, 'SLOW request');
  await sleep(1500);
  dump('A1-working');
  const a = await push('MSG-A', 'MSG-A: please also check the README.');
  const aStart = Date.now();
  await until(() => modelSaw('MSG-A'), slowMs + 20000, 'MSG-A at model');
  const aReq = modelSaw('MSG-A');
  say('A RESULT: send answer=' + a.delivered + ' model saw MSG-A ' + (aReq ? ((aReq.at - aStart) / 1000).toFixed(1) + 's after send, role=' + aReq.last.role : 'NEVER') +
    '; taken=' + JSON.stringify(taken.filter(x => x.id === 'MSG-A').map(x => x.via)));
  await sleep(4000);
  dump('A2-after');

  // B: message to an idle agent -> typed in once the screen is quiet
  const b = await push('MSG-B', 'MSG-B: status report please.');
  const bStart = Date.now();
  await until(() => modelSaw('MSG-B'), 20000, 'MSG-B at model');
  const bReq = modelSaw('MSG-B');
  say('B RESULT: send answer=' + b.delivered + ' model saw MSG-B ' + (bReq ? ((bReq.at - bStart) / 1000).toFixed(1) + 's after send, role=' + bReq.last.role : 'NEVER') +
    '; taken=' + JSON.stringify(taken.filter(x => x.id === 'MSG-B').map(x => x.via)));
  await sleep(4000);
  dump('B2-after');

  // C: the person has a draft in the prompt box -> nothing typed; the draft's own turn carries it
  for (const ch of 'my own draft') { sendInput(ch); await sleep(30); }
  await sleep(500);
  const c = await push('MSG-C', 'MSG-C: one more thing.');
  await sleep(6000);
  dump('C1-draft-kept');
  say('C RESULT (while draft): model saw MSG-C=' + Boolean(modelSaw('MSG-C')) + ' inbox still holds=' + inbox.map(m => m.id).join(','));
  sendInput('\r');
  await until(() => modelSaw('MSG-C'), 20000, 'MSG-C at model');
  const draftReq = modelRequests.findIndex(r => r.last.text.includes('my own draft'));
  const cReq = modelRequests.findIndex(r => r.last.text.includes('MSG-C'));
  say('C RESULT: send answer=' + c.delivered + ' draft request #' + (draftReq + 1) + ' (contains MSG-C: ' + (modelRequests[draftReq]?.last.text.includes('MSG-C')) + '), MSG-C request #' + (cReq + 1) +
    '; taken=' + JSON.stringify(taken.filter(x => x.id === 'MSG-C').map(x => x.via)));
  await sleep(4000);
  dump('C2-after');

  // D: priority path unchanged
  const d = await push('MSG-D', 'MSG-D: priority note.', { priority: true });
  await until(() => modelSaw('MSG-D'), 20000, 'MSG-D at model');
  say('D RESULT: priority answer=' + d.delivered + ' model saw MSG-D=' + Boolean(modelSaw('MSG-D')));
  await sleep(3000);
  dump('D-after');

  say('SUMMARY taken=' + JSON.stringify(taken.map(x => x.id + ':' + x.via)) + ' requests=' + modelRequests.length +
    ' each message seen by model in N requests: ' + ['MSG-A', 'MSG-B', 'MSG-C', 'MSG-D'].map(id => id + '=' + modelRequests.filter(r => !r.isTitle && r.last.text.includes(id)).length).join(' '));
}

main().catch(e => say('ERROR ' + e.stack)).finally(async () => {
  await sleep(500);
  try { process.kill(-proc.pid, 'SIGTERM'); } catch {}
  await sleep(1500);
  try { process.kill(-proc.pid, 'SIGKILL'); } catch {}
  process.exit(0);
});
