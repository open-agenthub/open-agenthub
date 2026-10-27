'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn, execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(process.argv[2]);
assert.match(root, /^\/tmp\/[A-Za-z0-9/_-]+$/);
const tag = process.argv[3] || 'open-agenthub-0.4.0';
assert.ok(['open-agenthub-0.1.0', 'open-agenthub-0.4.0'].includes(tag));
const home = path.join(root, 'home');
fs.mkdirSync(path.join(home, '.claude/backups'), { recursive: true });
fs.mkdirSync(path.join(home, '.claude/projects'), { recursive: true });
fs.mkdirSync(path.join(root, 'bin'), { recursive: true });
fs.writeFileSync(path.join(root, 'bin/claude'), '#!/bin/sh\nexec sleep 600\n', { mode: 0o700 });
fs.writeFileSync(path.join(home, '.claude/.credentials.json'), JSON.stringify({ claudeAiOauth: { accessToken: 'synthetic-access-only', refreshToken: 'synthetic-refresh-only' } }));
fs.writeFileSync(path.join(home, '.claude/backups/config.json'), '{"mcpServers":{"fixture":{"env":{"TOKEN":"synthetic-mcp-only"}}}}');
fs.writeFileSync(path.join(home, '.claude/projects/history.jsonl'), '{"text":"keep history"}\n');
fs.writeFileSync(path.join(home, '.claude/binary'), Buffer.from([0, 255, 13, 10]));
let source = execFileSync('git', ['show', `${tag}:agent-runtime/session-agent/server.js`], { encoding: 'utf8' });
// Only relocate the historical server's shared scratch names; archive logic is unchanged.
source = source.replaceAll('/tmp/state.tgz', path.join(root, 'state.tgz'))
  .replaceAll('/tmp/scrollback.log', path.join(root, 'scrollback.log'))
  .replaceAll('/tmp/.state-restored', path.join(root, 'unused-restored'));
fs.writeFileSync(path.join(root, 'server.cjs'), source + '\nconsole.log("PROOF_READY");\n');
const baseEnv = Object.fromEntries(['PATH', 'LD_LIBRARY_PATH', 'LANG', 'LC_ALL', 'TERM'].filter(k => process.env[k] !== undefined).map(k => [k, process.env[k]]));
let uploaded = false;
const server = http.createServer((req, res) => {
  const chunks = []; req.on('data', c => chunks.push(c));
  req.on('end', () => { assert.equal(req.url, '/state'); fs.writeFileSync(path.join(root, 'old.tgz'), Buffer.concat(chunks)); uploaded = true; res.end(); });
});
server.listen(0, '127.0.0.1', () => {
  const child = spawn(process.execPath, [path.join(root, 'server.cjs')], { env: {
    ...baseEnv, HOME: home, PATH: path.join(root, 'bin') + ':' + baseEnv.PATH,
    NODE_PATH: process.env.NODE_PATH || '/opt/session-agent/node_modules', AGENTHUB_PORT: '0',
    AGENTHUB_WORKDIR: home, AGENTHUB_STATE_PUT_URL: `http://127.0.0.1:${server.address().port}/state`
  }, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '', stopping = false;
  const timeout = setTimeout(() => { child.kill('SIGKILL'); throw new Error('Historical producer timeout'); }, 30000);
  child.stdout.on('data', data => { output += data; if (!stopping && output.includes('PROOF_READY')) { stopping = true; child.kill('SIGTERM'); } });
  child.stderr.on('data', data => { output += data; });
  child.on('exit', code => {
    clearTimeout(timeout); server.close(); assert.equal(code, 0, output); assert.ok(uploaded);
    const listing = execFileSync('tar', ['tzf', path.join(root, 'old.tgz')], { encoding: 'utf8' });
    assert.ok(listing.includes('.claude/.credentials.json'));
    console.log('PASS real ' + tag + ' session server archived synthetic OAuth login file');
  });
});
