// Uses the pinned native CLI/server and real PTY transport; only model responses and hub
// callbacks are deterministic stand-ins. Native history is never inserted by this proof.
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
const repo = path.resolve(import.meta.dirname, '../..');
const out = process.env.PROOF_OUT;
const cli = process.env.OPENCODE_BIN;
assert(out && cli, 'set PROOF_OUT and OPENCODE_BIN');
fs.mkdirSync(out, { recursive: true });
const home = path.join(out, 'home');
const work = path.join(out, 'work');
fs.mkdirSync(path.join(home, '.local/share'), { recursive: true });
fs.mkdirSync(path.join(home, '.opencode'), { recursive: true });
fs.mkdirSync(path.join(home, '.config/opencode'), { recursive: true });
fs.mkdirSync(work, { recursive: true });
try { fs.symlinkSync(path.join(home, '.opencode'), path.join(home, '.local/share/opencode')); } catch {}
const children = [];
process.once('SIGTERM', () => { for (const child of children) child.kill('SIGTERM'); process.exit(0); });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(fn, seconds = 120) {
  for (let n = 0; n < seconds * 5; n++) { try { const result = await fn(); if (result) return result; } catch {} await delay(200); }
  throw new Error('proof timed out');
}
function launch(command, args, env, name) {
  const log = fs.openSync(path.join(out, name + '.log'), 'a');
  const child = spawn(command, args, { env, cwd: work, stdio: ['ignore', log, log] });
  children.push(child); return child;
}
let policyCalls = 0;
const callbackToken = 'callback-token-reviews-7f3c9e';
const mcpToken = 'mcp-owner-credential-5b1d2a';
const hub = http.createServer(async (req, res) => {
  for await (const _ of req) {}
  res.setHeader('content-type', 'application/json');
  if (req.url.endsWith('/agent-policy')) { policyCalls++; res.end('{"decision":"allow"}'); }
  else if (req.url.endsWith('/transcript') && req.method === 'GET') res.end('');
  else res.end('{}');
});
await new Promise(resolve => hub.listen(18562, '127.0.0.1', resolve));
const model = http.createServer(async (req, res) => {
  let raw = ''; for await (const chunk of req) raw += chunk;
  const body = JSON.parse(raw);
  const user = body.messages?.filter(m => m.role === 'user').at(-1);
  const text = typeof user?.content === 'string' ? user.content : JSON.stringify(user?.content);
  if (!body.stream) { res.setHeader('content-type', 'application/json'); res.end(JSON.stringify({ id: 'title', choices: [{ message: { role: 'assistant', content: 'Native history proof' }, finish_reason: 'stop' }] })); return; }
  res.setHeader('content-type', 'text/event-stream');
  const send = delta => res.write('data: ' + JSON.stringify({ id: 'proof', object: 'chat.completion.chunk', created: 1, model: 'm1', choices: [{ index: 0, ...delta }] }) + '\n\n');
  const lastUser = body.messages.findLastIndex(m => m.role === 'user');
  const toolDone = body.messages.slice(lastUser + 1).some(m => m.role === 'tool');
  if (text?.includes('TOOL_PROOF') && !toolDone) {
    send({ delta: { role: 'assistant', tool_calls: [{ index: 0, id: 'native_call', type: 'function', function: { name: 'bash', arguments: JSON.stringify({ command: 'echo native-tool-success', description: 'Verify native tool history' }) } }] }, finish_reason: null });
    send({ delta: {}, finish_reason: 'tool_calls' });
  } else {
    send({ delta: { role: 'assistant', content: `Native answer ${text} ${text?.includes('MASK_PROOF') ? callbackToken + ' ' + mcpToken : ''}` }, finish_reason: null });
    send({ delta: {}, finish_reason: 'stop' });
  }
  res.end('data: [DONE]\n\n');
});
await new Promise(resolve => model.listen(18561, '127.0.0.1', resolve));
const config = {
  model: 'proof/m1', autoupdate: false, share: 'disabled', permission: 'allow',
  plugin: ['file://' + path.join(repo, 'agent-runtime/opencode/policy-plugin.mjs')],
  provider: { proof: { npm: '@ai-sdk/openai-compatible', name: 'Proof', options: { baseURL: 'http://127.0.0.1:18561/v1', apiKey: 'synthetic' }, models: { m1: { name: 'Proof', tool_call: true } } } }
};
const managed = path.join(out, 'managed');
fs.mkdirSync(managed, { recursive: true });
fs.writeFileSync(path.join(managed, 'opencode.json'), JSON.stringify({ plugin: config.plugin, permission: 'allow' }), { mode: 0o444 });
delete config.plugin;
fs.writeFileSync(path.join(home, '.config/opencode/opencode.json'), JSON.stringify(config));
const env = { ...process.env, HOME: home, PATH: path.dirname(cli) + ':' + process.env.PATH,
  AGENTHUB_MODE: 'autonomous', AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:18562/internal/sessions/reviews',
  AGENTHUB_CALLBACK_TOKEN: callbackToken, AGENTHUB_MCP_CONFIG: '', OPENCODE_TEST_MANAGED_CONFIG_DIR: managed, OPENCODE_DISABLE_DEFAULT_PLUGINS: '1', OPENCODE_DISABLE_MODELS_FETCH: '1', OPENCODE_DISABLE_FFF: '1', OPENCODE_EXPERIMENTAL_DISABLE_FILEWATCHER: '1', OPENCODE_DISABLE_AUTOUPDATE: '1', OPENCODE_DISABLE_SHARE: '1' };
try {
  const native = launch(cli, ['serve', '--hostname', '127.0.0.1', '--port', '18563'], env, 'native');
  console.log('Waiting for native health');
  await until(async () => { const response = await fetch('http://127.0.0.1:18563/global/health', { signal: AbortSignal.timeout(2000) }); return response.ok && (await response.json()).healthy; });
  console.log('Native health ready');
  await delay(500);
  assert.equal(native.exitCode, null, 'native server must own the proof port');
  const api = async (route, body) => {
    const response = await fetch('http://127.0.0.1:18563' + route + '?directory=' + encodeURIComponent(work), { method: body ? 'POST' : 'GET', signal: AbortSignal.timeout(180_000), headers: { 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
    assert(response.ok, route + ': ' + response.status); return response.json();
  };
  console.log('Creating native session');
  const session = await api('/session', { title: 'Native conversation proof' });
  console.log('Created native session', session.id);
  const require = createRequire(import.meta.url);
  const driver = require(path.join(repo, 'agent-runtime/opencode/driver.js'));
  for (let turn = 0; turn < 110; turn++) {
    const pendingTurn = api(`/session/${session.id}/message`, { parts: [{ type: 'text', text: turn === 0 ? 'TOOL_PROOF first native turn' : `Native turn ${turn}` }] });
    // Exercise native WAL reads while the real CLI is writing the current turn.
    driver.findTranscript({ env, home, fs });
    await pendingTurn;
    if (turn % 25 === 0) console.log('Native turns completed:', turn + 1);
  }
  await api(`/session/${session.id}/message`, { parts: [{ type: 'text', text: 'MASK_PROOF newest native turn' }] });
  const messages = await api(`/session/${session.id}/message`);
  assert(messages.length > 200);
  assert(policyCalls > 0, 'managed gate must have run');
  native.kill('SIGTERM'); await new Promise(resolve => native.once('exit', resolve));
  let file;
  for (let n = 0; n < 5; n++) file = driver.findTranscript({ env, home, fs });
  const original = fs.readFileSync(file, 'utf8');
  assert(original.includes('native-tool-success'));
  assert(original.includes('tool_use') && original.includes('tool_result'));
  assert(original.includes('Native turn 109'));
  assert.equal(original.trim().split('\n').length, messages.length);
  const agentEnv = { ...env, NODE_PATH: process.env.PROOF_NODE_MODULES || '/opt/session-agent/node_modules',
    AGENTHUB_DRIVER: path.join(repo, 'agent-runtime/opencode/driver.js'), AGENTHUB_PORT: '18564',
    AGENTHUB_WORKDIR: work, AGENTHUB_FILE_ROOT: path.join(out, 'files'), AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1', AGENTHUB_MODE: 'interactive', AGENTHUB_PROMPT: '' };
  const agent = launch(process.execPath, [path.join(repo, 'agent-runtime/session-agent/server.js')], agentEnv, 'agent');
  const get = async () => {
    const response = await fetch('http://127.0.0.1:18564/agenthub/transcript?from=0', { headers: { 'X-Agent-Token': callbackToken } });
    assert(response.ok); return response.text();
  };
  const runtimeRequire = createRequire(path.join(agentEnv.NODE_PATH, '../package.json'));
  const WebSocket = runtimeRequire('ws');
  const { Terminal } = runtimeRequire('@xterm/headless');
  const terminal = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
  await until(async () => { const response = await fetch('http://127.0.0.1:18564/agenthub/transcript', { headers: { 'X-Agent-Token': callbackToken } }); await response.text(); return response.ok; });
  const socket = new WebSocket('ws://127.0.0.1:18564/');
  socket.on('message', data => terminal.write(data.toString()));
  terminal.onData(data => { if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'input', data })); });
  await new Promise((resolve, reject) => { socket.once('open', resolve); socket.once('error', reject); });
  const transcript = await until(async () => { const text = await get(); return text.includes('MASK_PROOF newest native turn') && text; });
  assert(transcript.includes('first native turn') && transcript.includes('native-tool-success'));
  assert.equal(new Set(transcript.trim().split('\n').map(line => JSON.parse(line).uuid)).size, transcript.trim().split('\n').length);
  fs.writeFileSync(path.join(out, 'transcript.jsonl'), transcript);
  await delay(4000);
  const screen = Array.from({ length: terminal.rows }, (_, n) => terminal.buffer.active.getLine(terminal.buffer.active.viewportY + n)?.translateToString(true) || '').join('\n');
  fs.writeFileSync(path.join(out, 'terminal.txt'), screen);
  fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify({ nativeMessages: messages.length, policyCalls, reconnect: true, tools: true, nativeSession: session.id, agentPort: 18564 }, null, 2));
  console.log('Native CLI/server and runtime reconnect proof passed; agent remains available for backend proof on :18564');
  if (process.env.PROOF_KEEP_RUNNING === '1') await new Promise(resolve => process.once('SIGTERM', resolve));
  socket.close(); terminal.dispose();
  agent.kill('SIGTERM');
} finally {
  for (const child of children) child.kill('SIGTERM');
  model.close(); hub.close();
}
