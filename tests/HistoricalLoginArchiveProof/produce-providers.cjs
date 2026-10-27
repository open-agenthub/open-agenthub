'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn, execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(process.argv[2]), agent = process.argv[3];
const stores = {
  codex: ['.codex/auth.json'], cursor: ['.cursor/auth.json'],
  openclaw: ['.openclaw/auth-profiles.json', '.openclaw/agents/main/agent/auth-profiles.json', '.openclaw/agents/other/agent/openclaw-agent.sqlite', '.openclaw/agents/other/agent/openclaw-agent.sqlite-wal', '.openclaw/agents/other/agent/openclaw-agent.sqlite-shm'],
  opencode: ['.opencode/auth.json', '.opencode/mcp-auth.json']
};
assert.ok(Object.hasOwn(stores, agent));
const home = path.join(root, 'home'), stateDir = '.' + agent;
for (const name of stores[agent]) {
  fs.mkdirSync(path.dirname(path.join(home, name)), { recursive: true });
  fs.writeFileSync(path.join(home, name), 'synthetic-login-only');
}
fs.mkdirSync(path.join(home, stateDir, 'projects'), { recursive: true });
fs.writeFileSync(path.join(home, stateDir, 'projects/history.jsonl'), '{"text":"retain history"}\n');
fs.writeFileSync(path.join(home, stateDir, 'projects/auth.json'), 'keep unrelated same-name file');
const baseEnv = Object.fromEntries(['PATH', 'LD_LIBRARY_PATH', 'LANG', 'LC_ALL', 'TERM'].filter(k => process.env[k] !== undefined).map(k => [k, process.env[k]]));
const repo = path.resolve(__dirname, '../..');
let uploaded = false;
const server = http.createServer((req, res) => {
  assert.ok(!req.headers['x-agent-token'] || req.headers['x-agent-token'] === 'synthetic-callback-only', 'Unexpected fixture credential');
  const chunks = []; req.on('data', c => chunks.push(c));
  req.on('end', () => { if (req.url === '/state') { fs.writeFileSync(path.join(root, 'old.tgz'), Buffer.concat(chunks)); uploaded = true; } res.end(); });
});
server.listen(0, '127.0.0.1', () => {
  // Released non-Claude drivers exclude these files. Disable exclusions only to test defensive cleanup of foreign/legacy uploads.
  const source = `const {createCommonServer}=require(${JSON.stringify(path.join(repo, 'agent-runtime/common/server.js'))});
const driver={...require(${JSON.stringify(path.join(repo, 'agent-runtime/claude/driver.js'))}),name:'Fixture',stateDir:${JSON.stringify(stateDir)},authFilename:'unused-fixture-auth',stateExcludes:[],prepare(){},findTranscript(){return null},buildCommand(){return {cmd:'bash',args:['-c','sleep 600']}}};
createCommonServer({driver,dependencies:require(${JSON.stringify(path.join(repo, 'tests/ClaudeStateCleanupProof/isolated-archive.cjs'))}).dependencies(${JSON.stringify(root)})});console.log('PROOF_READY');`;
  const child = spawn(process.execPath, ['-e', source], { env: { ...baseEnv, HOME: home,
    NODE_PATH: process.env.NODE_PATH || '/opt/session-agent/node_modules', AGENTHUB_PORT: '0', AGENTHUB_WORKDIR: home,
    AGENTHUB_WORKSPACE_ROOT: home, AGENTHUB_FILE_ROOT: path.join(root, 'files'), AGENTHUB_SKILLS_SYNC_SECONDS: '0',
    AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(root, 'unused-watcher.pid'), AGENTHUB_SESSION_ID: 'synthetic-319',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-only', AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${server.address().port}/internal/sessions/synthetic-319`,
    AGENTHUB_STATE_PUT_URL: `http://127.0.0.1:${server.address().port}/state`
  }, stdio: ['ignore', 'pipe', 'pipe'] });
  let log = '', stopping = false;
  const timer = setTimeout(() => { child.kill('SIGKILL'); throw new Error('Fixture timeout'); }, 30000);
  child.stdout.on('data', data => { log += data; if (!stopping && log.includes('PROOF_READY')) { stopping = true; child.kill('SIGTERM'); } });
  child.stderr.on('data', data => { log += data; });
  child.on('exit', code => {
    clearTimeout(timer); server.close(); assert.equal(code, 0, log); assert.ok(uploaded);
    const listing = execFileSync('tar', ['tzf', path.join(root, 'old.tgz')], { encoding: 'utf8' });
    for (const name of stores[agent]) assert.ok(listing.includes(name));
    console.log('PASS real common server defensive ' + agent + ' fixture; released driver exclusions deliberately disabled');
  });
});
