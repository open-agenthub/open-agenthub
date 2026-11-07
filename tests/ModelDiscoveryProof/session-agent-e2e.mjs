// End-to-end proof of in-pod model discovery (#461) with the branch's real common/server.js, the
// real Codex driver (its interactive process swapped for a stand-in, codex-standin-driver.js) and
// the pinned Codex CLI as the probe, against the recording mock (mock.mjs on :18181):
//   1. the post-start push reaches a stand-in hub with account A's list;
//   2. an account switch through PUT /agenthub/credentials makes the next GET answer for B;
//   3. a refused account comes back as `fallback` / `not_entitled`, not as its list;
//   4. a custom endpoint is listed from the pod with its key, and a refused key is `not_entitled`;
//   5. no credential appears in any response, hub request or server log.
// Usage: node session-agent-e2e.mjs <worktree> <dir with codex on PATH>   (mock.mjs must be running)
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';

const [wt, binDir] = process.argv.slice(2);
const here = path.dirname(new URL(import.meta.url).pathname);
const work = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'model-proof-'));
const token = 'proof-callback-token-0123456789';
const results = [];
const secrets = [token, 'rt-SECRET', 'sk-test-AAAA1111', 'sk-test-deny-2222'];
const logs = [];
// Fresh ports per run: a server from the previous run may still be persisting on SIGTERM.
const base = 20000 + Math.floor(Math.random() * 20000);
const children = [];

function check(name, ok, detail = '') {
  // Details quote pod and mock answers: one line each, so an answer cannot forge a PASS line.
  detail = String(detail).replace(/[\r\n\u2028\u2029]+/g, ' ');
  results.push({ name, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`);
}

function account(dir, id) {
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(dir, { recursive: true });
  const b = o => Buffer.from(JSON.stringify(o)).toString('base64url');
  const jwt = b({ alg: 'none' }) + '.' + b({ email: id + '@example.com', exp: Math.floor(Date.now() / 1000) + 86400,
    'https://api.openai.com/auth': { chatgpt_plan_type: 'plus', chatgpt_account_id: id, chatgpt_user_id: 'u-' + id } }) + '.sig';
  secrets.push(jwt);
  return JSON.stringify({ OPENAI_API_KEY: null, tokens: { id_token: jwt, access_token: jwt, refresh_token: 'rt-SECRET', account_id: id },
    last_refresh: new Date().toISOString() });
}

// The hub: records what the pod reports, answers everything else with 204.
const hubPosts = [];
const hub = http.createServer((req, res) => {
  let body = '';
  req.on('data', d => body += d);
  req.on('end', () => {
    logs.push(`${req.method} ${req.url} ${req.headers['x-agent-token'] === token ? 'token-ok' : 'no-token'} ${body}`);
    if (req.url.endsWith('/models')) hubPosts.push(JSON.parse(body));
    res.writeHead(204); res.end();
  });
});
await new Promise(r => hub.listen(0, '127.0.0.1', r));
const hubUrl = `http://127.0.0.1:${hub.address().port}/internal/sessions/proof`;

async function startServer(name, port, extraEnv) {
  const home = path.join(work, name);
  const codexHome = path.join(home, '.codex');
  fs.mkdirSync(codexHome, { recursive: true });
  const env = {
    PATH: `${binDir}:${path.dirname(process.execPath)}:/usr/bin:/bin`, HOME: home, CODEX_HOME: codexHome, TMPDIR: work,
    AGENTHUB_DRIVER: path.join(here, 'codex-standin-driver.js'), AGENTHUB_PROOF_WORKTREE: wt, AGENTHUB_PORT: String(port), AGENTHUB_SESSION_ID: 'proof',
    AGENTHUB_MODE: 'interactive', AGENTHUB_RESUME: '0', AGENTHUB_PROMPT: '', AGENTHUB_SKILLS_SYNC_SECONDS: '0',
    AGENTHUB_WORKDIR: home, AGENTHUB_WORKSPACE_ROOT: home, AGENTHUB_FILE_ROOT: path.join(home, 'files'),
    AGENTHUB_CALLBACK_URL: hubUrl, AGENTHUB_CALLBACK_TOKEN: token, AGENTHUB_MODEL_DISCOVERY_DELAY_MS: '3000',
    NODE_PATH: path.join(wt, 'agent-runtime/session-agent/node_modules'), ...extraEnv
  };
  // The Codex config the entrypoint would write: the mock stands in for the provider's service.
  fs.writeFileSync(path.join(codexHome, 'config.toml'), extraEnv.CONFIG || '');
  delete env.CONFIG;
  if (extraEnv.AUTH) fs.writeFileSync(path.join(codexHome, 'auth.json'), extraEnv.AUTH);
  delete env.AUTH;
  const child = spawn(process.execPath, [path.join(wt, 'agent-runtime/common/server.js')], { env, stdio: ['ignore', 'pipe', 'pipe'] });
  children.push(child);
  child.stdout.on('data', d => logs.push(String(d)));
  child.stderr.on('data', d => logs.push(String(d)));
  for (let i = 0; i < 100; i++) {
    try { await fetch(`http://127.0.0.1:${port}/agenthub/models`, { method: 'HEAD' }); break; } catch { await new Promise(r => setTimeout(r, 100)); }
  }
  return child;
}

async function models(port, refresh = false) {
  const response = await fetch(`http://127.0.0.1:${port}/agenthub/models${refresh ? '?refresh=1' : ''}`, { headers: { 'X-Agent-Token': token } });
  const text = await response.text();
  logs.push(text);
  return { status: response.status, body: JSON.parse(text) };
}

const ids = catalog => catalog.models.map(m => m.id).join(',');
const subscriptionConfig = 'chatgpt_base_url = "http://127.0.0.1:18181/backend-api/"\nopenai_base_url = "http://127.0.0.1:18181/backend-api/codex"\n';

// ---- subscription accounts ----
const authA = account(path.join(work, 'a'), 'acct-1');
const authB = account(path.join(work, 'b'), 'acct-2');
const authDeny = account(path.join(work, 'd'), 'acct-deny');
const sub = await startServer('sub', base, { AGENTHUB_AUTH_MODE: 'subscription', CONFIG: subscriptionConfig, AUTH: authA });
for (let i = 0; i < 300 && hubPosts.length === 0; i++) await new Promise(r => setTimeout(r, 100));
check('post-start push reaches the hub with account A\'s list', hubPosts[0] && ids(hubPosts[0]) === 'gpt-6.1-sol,gpt-6-luna' && hubPosts[0].verified === true,
  hubPosts[0] ? `${hubPosts[0].state} ${ids(hubPosts[0])}` : 'no push');
check('the push carries the callback token', logs.some(l => l.startsWith('POST /internal/sessions/proof/models token-ok')));
check('unauthenticated GET is refused', (await fetch(`http://127.0.0.1:${base}/agenthub/models`)).status === 401);

let answer = await models(base);
check('GET answers account A from the pod cache', ids(answer.body) === 'gpt-6.1-sol,gpt-6-luna', ids(answer.body));

const put = async auth => { const r = await fetch(`http://127.0.0.1:${base}/agenthub/credentials`, { method: 'PUT',
  headers: { 'X-Agent-Token': token, 'X-Agent-Provider': 'codex', 'Content-Type': 'application/json' }, body: auth }); await r.text(); return r.status; };
check('account switch to B is installed', [200, 202].includes(await put(authB)));
answer = await models(base);
check('after the switch the same GET answers account B (no refresh flag)', ids(answer.body) === 'gpt-6-astra' && answer.body.verified, ids(answer.body));

check('account switch to a refused account is installed', [200, 202].includes(await put(authDeny)));
answer = await models(base);
check('a refused account is a fallback with not_entitled, not its list',
  answer.body.state === 'fallback' && answer.body.verified === false && answer.body.error?.code === 'not_entitled',
  `${answer.body.state} ${answer.body.error?.code}: ${answer.body.error?.message}`);
sub.kill();

// ---- custom endpoint ----
const endpointEnv = key => ({ AGENTHUB_AUTH_MODE: 'customendpoint', AGENTHUB_MODEL_ENDPOINT_URL: 'http://127.0.0.1:18181/v1',
  AGENTHUB_MODEL_NAME: 'mock-a2', AGENTHUB_MODEL_API_KEY: key, AGENTHUB_MODEL_DISCOVERY: '0' });
const ep = await startServer('endpoint', base + 1, endpointEnv('sk-test-AAAA1111'));
answer = await models(base + 1);
check('a custom endpoint is listed from the pod with its key', answer.body.source === 'endpoint' && ids(answer.body) === 'mock-a1,mock-a2'
  && answer.body.models.find(m => m.id === 'mock-a2')?.isDefault === true, ids(answer.body));
ep.kill();
const denied = await startServer('endpoint-deny', base + 2, endpointEnv('sk-test-deny-2222'));
answer = await models(base + 2);
check('a refused endpoint key is not_entitled', answer.body.state === 'error' && answer.body.error?.code === 'not_entitled', answer.body.error?.message);
denied.kill();

// ---- secrets ----
await new Promise(r => setTimeout(r, 300));
const everything = logs.join('\n');
const leaked = secrets.filter(secret => everything.includes(secret));
check('no credential in responses, hub requests or server logs', leaked.length === 0, leaked.length ? `${leaked.length} leaked` : `${secrets.length} values scanned`);

for (const child of children) if (child.exitCode === null) { child.kill('SIGKILL'); }
hub.close();
fs.rmSync(work, { recursive: true, force: true });
const failed = results.filter(r => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
