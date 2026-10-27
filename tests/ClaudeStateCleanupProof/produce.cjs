'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn, execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const repo = path.resolve(__dirname, '../..');
const root = process.argv[2];
const legacy = !process.argv.includes('--current');
// A real server must never inherit the session's callback credentials or upload destinations.
const childBaseEnv = Object.fromEntries(['PATH', 'LD_LIBRARY_PATH', 'LANG', 'LC_ALL', 'TERM']
  .filter(key => process.env[key] !== undefined).map(key => [key, process.env[key]]));
if (!root) throw new Error('Pass a disposable proof directory');
fs.mkdirSync(root, { recursive: true });
const home = path.join(root, 'home');
fs.mkdirSync(path.join(home, '.claude/backups'), { recursive: true });
fs.mkdirSync(path.join(home, '.claude/projects'), { recursive: true });
fs.writeFileSync(path.join(home, '.claude/backups/config.json'), JSON.stringify({ mcpServers: { fixture: { headers: { Authorization: 'Bearer synthetic-proof-only' }, env: { SECRET: 'synthetic-env-only' } } } }));
fs.writeFileSync(path.join(home, '.claude/projects/conversation.jsonl'), '{"text":"retain history"}\n');
fs.writeFileSync(path.join(home, '.claude/binary'), Buffer.from([0, 255, 128, 13, 10]));
fs.writeFileSync(path.join(home, '.claude/' + 'long-name-'.repeat(25)), 'long name');
if (!fs.existsSync(path.join(home, '.claude/link'))) fs.symlinkSync('binary', path.join(home, '.claude/link'));
let uploaded = false;
const server = http.createServer((req, res) => {
  if (req.headers['x-agent-token']) assert.equal(req.headers['x-agent-token'], 'synthetic-callback-only');
  const chunks = [];
  req.on('data', chunk => chunks.push(chunk));
  req.on('end', () => {
    if (req.method === 'PUT' && req.url === '/state') {
      fs.writeFileSync(path.join(root, 'old.tgz'), Buffer.concat(chunks));
      uploaded = true;
    }
    res.end();
  });
});
server.listen(0, '127.0.0.1', () => {
  // Use the real server and real PTY; only the provider command and pre-fix excludes differ.
  const source = `const {createCommonServer}=require(${JSON.stringify(path.join(repo, 'agent-runtime/common/server.js'))});
const base=require(${JSON.stringify(path.join(repo, 'agent-runtime/claude/driver.js'))});
const driver={...base,stateExcludes:${legacy ? JSON.stringify(['.claude/plugins/marketplaces']) : 'base.stateExcludes'},prepare(){},buildCommand(){return {cmd:'bash',args:['-c','sleep 600']}}};
createCommonServer({driver}); console.log("PROOF_READY");`;
  const child = spawn(process.execPath, ['-e', source], { env: { ...childBaseEnv, HOME: home,
    NODE_PATH: process.env.NODE_PATH || '/opt/session-agent/node_modules', AGENTHUB_WORKDIR: home,
    AGENTHUB_WORKSPACE_ROOT: home, AGENTHUB_FILE_ROOT: path.join(root, 'files'),
    AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(root, 'unused-watcher.pid'),
    AGENTHUB_SESSION_ID: 'synthetic-316', AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-only',
    AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${server.address().port}/internal/sessions/synthetic-316`,
    AGENTHUB_STATE_PUT_URL: `http://127.0.0.1:${server.address().port}/state`,
    AGENTHUB_PORT: '0', AGENTHUB_SKILLS_SYNC_SECONDS: '0' }, stdio: ['ignore', 'pipe', 'pipe'] });
  let log = '', stopping = false;
  const timeout = setTimeout(() => { child.kill('SIGKILL'); throw new Error('Producer timeout: ' + log); }, 30000);
  child.stdout.on('data', data => {
    log += data;
    if (!stopping && log.includes('PROOF_READY')) { stopping = true; child.kill('SIGTERM'); }
  });
  child.stderr.on('data', data => { log += data; });
  child.on('exit', code => {
    clearTimeout(timeout); server.close();
    assert.equal(code, 0, log); assert.ok(uploaded, log);
    const listing = execFileSync('tar', ['tzf', path.join(root, 'old.tgz')], { encoding: 'utf8' });
    assert.equal(listing.includes('.claude/backups/'), legacy);
    console.log('PASS real common/server.js SIGTERM archive uploaded; ' + (legacy ? 'legacy synthetic backup present' : 'current driver excludes backups'));
  });
});
