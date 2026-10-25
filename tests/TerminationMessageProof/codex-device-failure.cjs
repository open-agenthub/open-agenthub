// What the session list shows when the real Codex device login fails after its code is on screen
// (#244, #287). Runs `codex login --device-auth` inside the real common/server.js of a chosen ref,
// with real node-pty, against the fake identity in scripts/proofs/242/mock-auth.cjs started with
// SIGNIN_DEVICE_TOKEN_STATUS=500. Prints the termination message and fails if the device code is
// in it. Nothing here is a real account.
//   CODEX_BIN=<pinned codex> SIGNIN_TLS_CA=<ca.pem from mock-auth> [SOURCE_REF=origin/main] \
//     node tests/TerminationMessageProof/codex-device-failure.cjs
'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync, spawn } = require('node:child_process');
const http = require('node:http');

const DEVICE_CODE = 'ABCD-12345'; // what mock-auth.cjs hands out
const repo = path.resolve(__dirname, '../..');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'termination-proof-'));
const ref = process.env.SOURCE_REF;
if (ref) {
  const archive = execFileSync('git', ['archive', ref, 'agent-runtime'], { cwd: repo, maxBuffer: 64 * 1024 * 1024 });
  execFileSync('tar', ['-xf', '-', '-C', root], { input: archive });
} else {
  fs.cpSync(path.join(repo, 'agent-runtime'), path.join(root, 'agent-runtime'),
    { recursive: true, filter: source => !source.includes('/node_modules') });
}
// The image layout: node_modules beside common/, with the runtime's real node-pty.
fs.symlinkSync(process.env.SESSION_AGENT_MODULES || '/opt/session-agent/node_modules', path.join(root, 'agent-runtime/node_modules'));

const home = path.join(root, 'home');
fs.mkdirSync(path.join(home, 'tmp'), { recursive: true, mode: 0o700 });
const termination = path.join(root, 'termination-log');
const env = Object.fromEntries(Object.entries(process.env)
  .filter(([key]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|.*TOKEN|.*KEY|.*SECRET)/.test(key)));
Object.assign(env, {
  HOME: home, TMPDIR: path.join(home, 'tmp'), CODEX_HOME: path.join(home, '.codex'),
  AGENTHUB_MODE: 'interactive', AGENTHUB_PORT: String(17900 + Math.floor(Math.random() * 90)),
  AGENTHUB_WORKDIR: home, AGENTHUB_SKILLS_SYNC_SECONDS: '0',
  AGENTHUB_WATCHER_PIDFILE: path.join(home, 'tmp', 'watcher.pid'),
  TERMINATION_MESSAGE_PATH: termination,
  HTTPS_PROXY: 'http://127.0.0.1:' + (process.env.SIGNIN_PROXY_PORT || '18443'),
  NODE_EXTRA_CA_CERTS: process.env.SIGNIN_TLS_CA, SSL_CERT_FILE: process.env.SIGNIN_TLS_CA,
  CODEX_CA_CERTIFICATE: process.env.SIGNIN_TLS_CA
});
fs.mkdirSync(env.CODEX_HOME, { recursive: true });
fs.writeFileSync(path.join(env.CODEX_HOME, 'config.toml'), 'cli_auth_credentials_store = "file"\n');

// A hub that accepts every callback; only the termination file is under test.
const hub = http.createServer((req, res) => { req.resume(); req.on('end', () => {
  res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{}'); }); });

const runner = `
const driver = require(${JSON.stringify(path.join(root, 'agent-runtime/codex/driver.js'))});
require(${JSON.stringify(path.join(root, 'agent-runtime/common/server.js'))}).createCommonServer({
  env: process.env,
  driver: { ...driver, buildCommand: () => ({ cmd: ${JSON.stringify(process.env.CODEX_BIN)}, args: ['login', '--device-auth'] }),
    isResumeCommand: () => false }
});`;
hub.listen(0, '127.0.0.1', () => {
Object.assign(env, { AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${hub.address().port}/internal/sessions/proof`,
  AGENTHUB_CALLBACK_TOKEN: 'proof-callback-token', AGENTHUB_SESSION_ID: 'proof' });
const child = spawn(process.execPath, ['-e', runner], { env, stdio: ['ignore', 'pipe', 'pipe'] });
let log = '';
child.stdout.on('data', d => { log += d; });
child.stderr.on('data', d => { log += d; });
const timer = setTimeout(() => child.kill('SIGKILL'), 60_000);
child.on('exit', code => {
  clearTimeout(timer);
  let message = '';
  try { message = fs.readFileSync(termination, 'utf8'); } catch {}
  console.log(`source: ${ref || 'working tree'}; session agent exit=${code}`);
  console.log('termination: ' + (message || '(none written)').replaceAll(DEVICE_CODE, '<DEVICE CODE>'));
  fs.rmSync(root, { recursive: true, force: true });
  if (!message) { console.log('FAIL: no termination message\n' + log.slice(-1500).replaceAll(DEVICE_CODE, '<DEVICE CODE>')); process.exitCode = 1; }
  else if (message.includes(DEVICE_CODE)) { console.log('FAIL: the device code is in the termination message'); process.exitCode = 1; }
  else console.log('PASS: the device code is not in the termination message');
  hub.close();
});
});
