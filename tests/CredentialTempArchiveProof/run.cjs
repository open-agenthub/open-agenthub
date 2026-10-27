'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const zlib = require('node:zlib');
const assert = require('node:assert/strict');
const { fork, execFileSync } = require('node:child_process');
const { once } = require('node:events');
const repo = path.resolve(__dirname, '../..');
const secret = 'synthetic-race-credential-only';

function waitForFile(file) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { watcher.close(); reject(new Error('Fixture barrier timeout')); }, 30000);
    const ready = () => { if (fs.existsSync(file)) { clearTimeout(timer); watcher.close(); resolve(); } };
    const watcher = fs.watch(path.dirname(file), ready);
    ready();
  });
}

async function runCase(parent, agent, baseline, expectLeak) {
  const root = fs.mkdtempSync(path.join(parent, agent + '-'));
  const home = path.join(root, 'home'); fs.mkdirSync(home);
  const base = require(path.join(repo, 'agent-runtime', agent, 'driver.js'));
  fs.mkdirSync(path.join(home, base.stateDir), { recursive: true });
  fs.writeFileSync(path.join(home, base.stateDir, 'history.jsonl'), '{"text":"retained history"}\n');
  fs.mkdirSync(path.join(root, 'bin'));
  fs.copyFileSync(path.join(__dirname, 'tar.cjs'), path.join(root, 'bin/tar'));
  fs.chmodSync(path.join(root, 'bin/tar'), 0o700);
  if (baseline) for (const [file, output] of [['credential-install.js', 'legacy-install.cjs'], ['state-archive.js', 'legacy-archive.cjs']])
    fs.writeFileSync(path.join(root, output), execFileSync('git', ['show', 'open-agenthub-1.3.0:agent-runtime/common/' + file], { cwd: repo }));
  let uploaded;
  const firstUpload = new Promise(resolve => { uploaded = resolve; });
  const receiver = http.createServer((req, res) => {
    assert.ok(!req.headers['x-agent-token'] || req.headers['x-agent-token'] === 'synthetic-callback-only', 'Unexpected fixture credential');
    const chunks = []; req.on('data', c => chunks.push(c));
    req.on('end', () => {
      if (req.method === 'PUT' && req.url === '/state') uploaded(Buffer.concat(chunks));
      res.end('{}');
    });
  });
  receiver.listen(0, '127.0.0.1'); await once(receiver, 'listening');
  const tarReady = waitForFile(path.join(root, 'tar-ready'));
  const baseEnv = Object.fromEntries(['PATH', 'LD_LIBRARY_PATH', 'LANG', 'LC_ALL', 'TERM'].filter(k => process.env[k] !== undefined).map(k => [k, process.env[k]]));
  const child = fork(path.join(__dirname, 'server.cjs'), [], { execArgv: [], env: {
    ...baseEnv, HOME: home, PATH: path.join(root, 'bin') + ':' + baseEnv.PATH,
    NODE_PATH: process.env.NODE_PATH || '/opt/session-agent/node_modules', PROOF_ROOT: root, PROOF_AGENT: agent, PROOF_BASELINE: baseline ? '1' : '0',
    AGENTHUB_WORKDIR: home, AGENTHUB_WORKSPACE_ROOT: home, AGENTHUB_FILE_ROOT: path.join(root, 'files'), AGENTHUB_PORT: '0',
    AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(root, 'unused-watcher.pid'), AGENTHUB_SKILLS_SYNC_SECONDS: '0',
    AGENTHUB_SESSION_ID: 'synthetic-321', AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-only',
    AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${receiver.address().port}/internal/sessions/synthetic-321`,
    AGENTHUB_STATE_PUT_URL: `http://127.0.0.1:${receiver.address().port}/state`
  }, stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
  let log = ''; child.stdout.on('data', data => { log += data; }); child.stderr.on('data', data => { log += data; });
  const deadline = setTimeout(() => child.kill('SIGKILL'), 35000);
  try {
    const [ready] = await once(child, 'message');
    await tarReady;
    const installed = fetch(`http://127.0.0.1:${ready.port}/agenthub/credentials/refresh`, {
      method: 'PUT', headers: { 'X-Agent-Token': 'synthetic-callback-only', 'X-Agent-Provider': 'fixture', 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: secret }), signal: AbortSignal.timeout(30000)
    });
    const [bytes, response] = await Promise.all([firstUpload, installed]);
    assert.equal(response.status, 200);
    const temporary = fs.readFileSync(path.join(root, 'credential-ready'), 'utf8');
    const raw = zlib.gunzipSync(bytes);
    assert.equal(raw.includes(Buffer.from(secret)), expectLeak, 'Unexpected credential capture');
    fs.writeFileSync(path.join(root, 'captured.tgz'), bytes);
    const listing = execFileSync('/usr/bin/tar', ['tzf', path.join(root, 'captured.tgz')], { encoding: 'utf8' });
    assert.equal(listing.split('\n').includes(temporary), expectLeak, 'Unexpected temp entry');
    assert.ok(listing.includes(base.stateDir + '/history.jsonl'));
    fs.writeFileSync(path.join(parent, agent + (baseline ? '-legacy.tgz' : '-fixed.tgz')), bytes);
    child.kill('SIGTERM');
    const [code] = await once(child, 'exit'); assert.equal(code, 0, log);
    console.log('PASS real periodic archive + credential refresh held before rename: ' + agent + ' captured=' + expectLeak);
    return { agent, baseline, captured: expectLeak, temporary };
  } finally {
    clearTimeout(deadline); if (child.exitCode === null) child.kill('SIGKILL'); receiver.close();
  }
}

async function main() {
  const root = path.resolve(process.argv[2]); assert.match(root, /^\/tmp\/[A-Za-z0-9/_-]+$/); fs.mkdirSync(root, { recursive: true });
  const baseline = process.argv.includes('--baseline'), expectLeak = process.argv.includes('--expect-leak');
  const results = [];
  for (const agent of ['claude', 'codex', 'cursor', 'openclaw', 'opencode']) results.push(await runCase(root, agent, baseline, expectLeak));
  fs.writeFileSync(path.join(root, 'results.json'), JSON.stringify(results, null, 2));
}
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
module.exports = { runCase };
