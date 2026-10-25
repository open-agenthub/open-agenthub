'use strict';

// #244: an Autonomous Codex session with auto-approve on, against the backend's real policy
// decision. The callback is tests/AutonomousPolicyProof (the real AgentPolicyMatcher), not a
// canned answer, because a canned "allow" is what hid the deny in the #160 proof. Runs the pinned
// CLI in exec mode with the driver's real arguments and the managed requirements bound via proot.
//   CODEX_BIN=… PROOT_BIN=… POLICY_URL=http://127.0.0.1:<port> node codex-exec-smoke.js
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, execFileSync } = require('node:child_process');
const { createServer } = require('node:http');
const driver = require('../../agent-runtime/codex/driver');
const proot = process.env.PROOT_BIN;
const codex = process.env.CODEX_BIN;
const policyUrl = process.env.POLICY_URL;
assert.ok(path.isAbsolute(proot || ''), 'PROOT_BIN must be absolute');
assert.ok(path.isAbsolute(codex || ''), 'CODEX_BIN must be absolute');
assert.ok(policyUrl, 'POLICY_URL must point at tests/AutonomousPolicyProof');
const runtime = path.resolve(__dirname, '../../agent-runtime/codex');
const pin = /ARG CODEX_VERSION=(\S+)/.exec(fs.readFileSync(path.join(runtime, 'Dockerfile'), 'utf8'))[1];
assert.equal(execFileSync(codex, ['--version'], { encoding: 'utf8' }).trim(), `codex-cli ${pin}`);
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-autonomous-policy-'));
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

// The commands of the failing smoke session: a redirect, a chain and a plain command.
const CASES = {
  redirect: 'echo smoke > smoke.txt && cat smoke.txt && uname -m',
  plain: 'uname -m',
  variable: 'echo "$HOME" | wc -c',
  git: 'git add -A && git -c user.name=smoke -c user.email=smoke@example.com commit -q --allow-empty -m smoke && git log --oneline -1',
  // A file edit is Codex's apply_patch tool, not a shell command.
  edit: { patch: '*** Begin Patch\n*** Add File: edited.txt\n+edited by apply_patch\n*** End Patch\n' }
};

async function run(label, command) {
  const work = path.join(root, label);
  const home = path.join(work, 'home');
  fs.mkdirSync(home, { recursive: true });
  execFileSync('git', ['init', '-q', work]);
  const scratch = path.join(work, 'scratch');
  fs.mkdirSync(scratch);
  fs.writeFileSync(path.join(home, 'config.toml'),
    `cli_auth_credentials_store = "file"\n[projects.${JSON.stringify(work)}]\ntrust_level = "trusted"\n`);
  fs.writeFileSync(path.join(home, 'auth.json'), JSON.stringify({ auth_mode: 'apikey', OPENAI_API_KEY: 'synthetic-key' }));
  const modelRequests = [];
  const server = createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      if (req.url !== '/v1/responses' || req.method !== 'POST') return res.writeHead(404).end();
      const payload = JSON.parse(body);
      modelRequests.push(payload);
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      res.end(stream(modelRequests.length === 1 ? (typeof command === 'string' ? {
        type: 'function_call', call_id: 'smoke', name: 'exec_command',
        arguments: JSON.stringify({ cmd: command, workdir: work })
      } : { type: 'custom_tool_call', call_id: 'smoke', name: 'apply_patch', input: command.patch }) : { type: 'message', role: 'assistant', id: 'done',
        content: [{ type: 'output_text', text: 'smoke complete' }] }, `response-${modelRequests.length}`));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const built = driver.buildCommand({ AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'Run the smoke commands.' }, false);
  const args = [...built.args];
  args.splice(5, 0, '--disable', 'enable_request_compression', '-c', 'model_provider="proof"',
    '-c', 'model_providers.proof.name="proof"', '-c', `model_providers.proof.base_url="${base}/v1"`,
    '-c', 'model_providers.proof.wire_api="responses"', '-c', 'model_providers.proof.env_key="CODEX_API_KEY"',
    '-c', 'model_providers.proof.supports_websockets=false', '-m', 'gpt-5.5');
  const env = { ...process.env, HOME: home, CODEX_HOME: home, TMPDIR: scratch, CODEX_API_KEY: 'synthetic-key',
    AGENTHUB_MODE: 'autonomous', AGENTHUB_CALLBACK_URL: `${policyUrl}/internal/sessions/proof`,
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback', AGENTHUB_SKILL_REMINDER: '0' };
  const child = spawn(proot, ['-b', `${requirements}:/etc/codex/requirements.toml`, codex, ...args],
    { cwd: work, env, detached: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  child.stdout.on('data', data => { output += data; });
  child.stderr.on('data', data => { output += data; });
  const timeout = setTimeout(() => { try { process.kill(-child.pid, 'SIGKILL'); } catch {} }, 60000);
  await new Promise(resolve => child.on('close', resolve));
  clearTimeout(timeout);
  server.close();
  const result = modelRequests[1] ? JSON.stringify(modelRequests[1].input.filter(i => /_call_output$/.test(i.type))) : '';
  fs.writeFileSync(path.join(root, `${label}.json`), JSON.stringify({ command, result, output,
    tools: (modelRequests[0]?.tools || []).map(t => `${t.type}:${t.name}`) }, null, 2));
  const blocked = /blocked by PreToolUse hook|session policy/i.test(result);
  console.log(`${label}: ${blocked ? 'BLOCKED' : 'ran'} :: ${typeof command === 'string' ? command : 'apply_patch edited.txt'}`);
  return { blocked, result };
}

(async () => {
  console.log(`Pinned CLI ${pin}; evidence: ${root}`);
  const outcomes = {};
  for (const [label, command] of Object.entries(CASES)) outcomes[label] = await run(label, command);
  for (const [label, outcome] of Object.entries(outcomes))
    assert.ok(!outcome.blocked, `${label} was blocked: ${outcome.result.slice(0, 400)}`);
  assert.match(outcomes.redirect.result, /smoke/);
  assert.ok(fs.existsSync(path.join(root, 'redirect', 'smoke.txt')), 'redirect never wrote smoke.txt');
  assert.match(outcomes.git.result, /smoke/);
  assert.equal(fs.readFileSync(path.join(root, 'edit', 'edited.txt'), 'utf8'), 'edited by apply_patch\n');
  console.log('all autonomous commands ran');
})().catch(error => { console.error(error.message); process.exitCode = 1; });
