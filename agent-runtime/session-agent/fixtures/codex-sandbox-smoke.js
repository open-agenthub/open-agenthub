'use strict';

// Manual Linux proof in a pod without user namespaces. proot binds branch-owned requirements
// for this process only, so the running session's image-owned policy remains untouched.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, execFileSync } = require('node:child_process');
const { createServer } = require('node:http');
const driver = require('../../codex/driver');
const pty = require(process.env.NODE_PTY_MODULE || 'node-pty');
const proot = process.env.PROOT_BIN;
const codex = process.env.CODEX_BIN;
assert.ok(path.isAbsolute(proot || ''), 'PROOT_BIN must be absolute');
assert.ok(path.isAbsolute(codex || ''), 'CODEX_BIN must be absolute');
const runtime = path.resolve(__dirname, '../../codex');
const pin = /ARG CODEX_VERSION=(\S+)/.exec(fs.readFileSync(path.join(runtime, 'Dockerfile'), 'utf8'))[1];
assert.equal(execFileSync(codex, ['--version'], { encoding: 'utf8' }).trim(), `codex-cli ${pin}`);
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-sandbox-smoke-'));
const requirements = path.join(root, 'requirements.toml');
fs.writeFileSync(requirements, fs.readFileSync(path.join(runtime, 'requirements.toml'), 'utf8')
  .replaceAll('/opt/session-agent/codex', runtime));

function event(type, data) { return `event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`; }
function stream(item, id) {
  return event('response.created', { response: { id } }) +
    event('response.output_item.done', { item }) +
    event('response.completed', { response: { id, usage: {
      input_tokens: 0, output_tokens: 0, total_tokens: 0,
      input_tokens_details: null, output_tokens_details: null
    } } });
}

async function run(label, { legacy = false, interactive = false, escalated = false, policy = 'allow', approval = 'allow' } = {}) {
  if (process.env.PROOF_ONLY && label !== process.env.PROOF_ONLY) return;
  const work = path.join(root, label);
  const home = path.join(work, 'home');
  fs.mkdirSync(home, { recursive: true });
  execFileSync('git', ['init', '-q', work]);
  const scratch = path.join(work, 'scratch');
  fs.mkdirSync(scratch);
  const marker = path.join(work, 'command-ran');
  const command = `printf executed > ${marker}`;
  fs.writeFileSync(path.join(home, 'config.toml'),
    `cli_auth_credentials_store = "file"\n[projects.${JSON.stringify(work)}]\ntrust_level = "trusted"\n`);
  fs.writeFileSync(path.join(home, 'auth.json'), JSON.stringify({ auth_mode: 'apikey', OPENAI_API_KEY: 'synthetic-key' }));
  fs.mkdirSync(path.join(work, '.codex'));
  // A checkout cannot disable the managed gate even when its commands run without an inner sandbox.
  fs.writeFileSync(path.join(work, '.codex/config.toml'), '[features]\nhooks = false\n');
  const requests = [];
  const modelRequests = [];
  const server = createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      if (req.url.endsWith('/agent-policy') || req.url.endsWith('/permission')) {
        assert.equal(req.headers['x-agent-token'], 'synthetic-callback');
        requests.push({ path: req.url.split('/').pop(), body: JSON.parse(body) });
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ decision: req.url.endsWith('/permission') ? approval : policy }));
      } else if (req.url === '/v1/responses' && req.method === 'POST') {
        const payload = JSON.parse(body);
        if (JSON.stringify(payload.input).includes('Generate a concise, single-line task title')) {
          res.writeHead(200, { 'Content-Type': 'text/event-stream' });
          res.end(stream({ type: 'message', role: 'assistant', id: 'title',
            content: [{ type: 'output_text', text: 'Run proof' }] }, 'title-response'));
          return;
        }
        modelRequests.push(payload);
        res.writeHead(200, { 'Content-Type': 'text/event-stream' });
        res.end(stream(modelRequests.length === 1 ? {
          type: 'function_call', call_id: 'sandbox-proof', name: 'exec_command',
          arguments: JSON.stringify({ cmd: command, ...(escalated ? { sandbox_permissions: 'require_escalated', justification: 'Run the synthetic sandbox proof?' } : {}) })
        } : { type: 'message', role: 'assistant', id: 'done',
          content: [{ type: 'output_text', text: 'proof complete' }] }, `response-${modelRequests.length}`));
      } else res.writeHead(404).end();
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const mode = interactive ? 'interactive' : 'autonomous';
  const built = driver.buildCommand({ AGENTHUB_MODE: mode, AGENTHUB_PROMPT: 'Run the proof command.' }, false);
  const args = legacy ? built.args.map(arg => arg === 'danger-full-access' ? 'workspace-write' : arg) : built.args;
  const config = ['--disable', 'enable_request_compression', '-c', 'model_provider="proof"',
    '-c', 'model_providers.proof.name="proof"', '-c', `model_providers.proof.base_url="${base}/v1"`,
    '-c', 'model_providers.proof.wire_api="responses"', '-c', 'model_providers.proof.env_key="CODEX_API_KEY"',
    '-c', 'model_providers.proof.supports_websockets=false', '-m', 'gpt-5.4'];
  args.splice(interactive ? 2 : 5, 0, ...config);
  const env = { ...process.env, HOME: home, CODEX_HOME: home, TMPDIR: scratch, CODEX_API_KEY: 'synthetic-key',
    AGENTHUB_MODE: mode, AGENTHUB_CALLBACK_URL: `${base}/internal/sessions/proof`,
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback', AGENTHUB_SKILL_REMINDER: '0', TERM: 'xterm-256color' };
  const boundArgs = ['-b', `${requirements}:/etc/codex/requirements.toml`, codex, ...args];
  let output = '';
  let timedOut = false;
  const child = interactive ? pty.spawn(proot, boundArgs, { cwd: work, env, cols: 120, rows: 40 })
    : spawn(proot, boundArgs, { cwd: work, env, detached: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const kill = () => { try { process.kill(-child.pid, 'SIGKILL'); } catch (error) { if (error.code !== 'ESRCH') throw error; } };
  const timeout = setTimeout(() => { timedOut = true; kill(); }, 30000);
  let done;
  if (interactive) {
    child.onData(data => {
      output += data;
      if (data.includes('\x1b[6n')) child.write('\x1b[1;1R');
      if (data.includes('\x1b[c')) child.write('\x1b[?1;2c');
      if (/Would you like to run|Do you want to run/.test(data)) child.write('1\r');
      if (output.includes('proof complete')) kill();
    });
    done = new Promise(resolve => child.onExit(resolve));
  } else {
    child.stdout.on('data', data => { output += data; });
    child.stderr.on('data', data => { output += data; });
    done = new Promise((resolve, reject) => { child.on('error', reject); child.on('close', resolve); });
  }
  try {
    const exit = await done;
    assert.ok(!timedOut, `${label}: timed out: ${output.slice(-2000)}`);
    if (!interactive) assert.equal(exit, 0, output);
    assert.ok(modelRequests.length >= 2, `${label}: tool result never reached model: ${output}`);
    const result = JSON.stringify(modelRequests[1].input);
    fs.writeFileSync(path.join(root, `${label}-result.json`), JSON.stringify({requests, modelRequests, output}, null, 2));
    const shouldRun = !legacy && (!interactive || escalated) && policy !== 'deny' && !(policy === 'ask' && approval === 'deny');
    assert.equal(fs.existsSync(marker), shouldRun, `${label}: unexpected execution`);
    if (!interactive) {
      assert.equal(requests[0].body.tool, 'Bash');
      assert.equal(requests[0].body.input.command, command);
    }
    if (legacy || (interactive && !escalated)) assert.match(result, /No permissions to create new namespace/);
    if (policy === 'deny' || (policy === 'ask' && approval === 'deny')) assert.match(result, /blocked by PreToolUse hook/);
    if (policy === 'ask') assert.deepEqual(requests.map(req => req.path), approval === 'allow'
      ? ['agent-policy', 'permission', 'agent-policy'] : ['agent-policy', 'permission']);
    const context = modelRequests[0].input.filter(item => item.role === 'developer').map(item => JSON.stringify(item)).join('\n');
    fs.writeFileSync(path.join(root, `${label}-context.txt`), context);
    console.log(`${label}: marker=${fs.existsSync(marker)}, callbacks=${requests.map(req => req.path).join(',')}`);
  } finally {
    clearTimeout(timeout);
    kill();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
}

(async () => {
  console.log(`Pinned CLI ${pin}; evidence: ${root}`);
  await run('legacy-workspace-write', { legacy: true });
  await run('full-access-allow');
  await run('full-access-deny', { policy: 'deny' });
  await run('full-access-approval-allow', { policy: 'ask' });
  await run('full-access-approval-deny', { policy: 'ask', approval: 'deny' });
  await run('interactive-default', { interactive: true });
  await run('interactive-escalated', { interactive: true, escalated: true });
})().catch(error => { console.error(error); process.exitCode = 1; });
