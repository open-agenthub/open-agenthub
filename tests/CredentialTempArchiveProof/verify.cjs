'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const { execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(process.argv[2]);
assert.match(root, /^\/tmp\/[A-Za-z0-9/_-]+$/);
const agentNames = { claude: 'Claude', codex: 'Codex', cursor: 'Cursor', opencode: 'OpenCode', openclaw: 'OpenClaw' };
const stores = Object.fromEntries(JSON.parse(fs.readFileSync(path.join(root, 'results.json'))).map(row => [agentNames[row.agent], [row.temporary]]));
const list = file => execFileSync('tar', ['tvzf', file], { encoding: 'utf8' }).trim().split('\n');
function hash(file) {
  const fd = fs.openSync(file, fs.constants.O_RDONLY | fs.constants.O_NOFOLLOW);
  try { assert.ok(fs.fstatSync(fd).isFile()); return crypto.hash('sha256', fs.readFileSync(fd)); }
  finally { fs.closeSync(fd); }
}
for (const [agent, secrets] of Object.entries(stores)) {
  const before = path.join(root, agent + '-before.tgz'), after = path.join(root, agent + '-after.tgz');
  const removed = name => secrets.includes(name) || (agent === 'Claude' && (name.replace(/\/$/, '') === '.claude/backups' || name.startsWith('.claude/backups/')));
  assert.deepEqual(list(after), list(before).filter(line => !secrets.some(name => line.endsWith(' ' + name)) && !(agent === 'Claude' && line.includes('.claude/backups'))));
  const unpacked = zlib.gunzipSync(fs.readFileSync(after)).toString('utf8');
  for (const value of ['synthetic-race-credential-only']) assert.ok(!unpacked.includes(value), 'Synthetic credential retained');
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'login-extract-proof-'));
  try {
    const dirs = ['before', 'after'].map(name => path.join(scratch, name));
    for (const dir of dirs) fs.mkdirSync(dir);
    execFileSync('tar', ['xzf', before, '-C', dirs[0]]); execFileSync('tar', ['xzf', after, '-C', dirs[1]]);
    function check(relative = '') {
      for (const name of fs.readdirSync(path.join(dirs[0], relative))) {
        const rel = path.join(relative, name); if (removed(rel)) continue;
        const original = path.join(dirs[0], rel), cleaned = path.join(dirs[1], rel);
        const expected = fs.lstatSync(original), actual = fs.lstatSync(cleaned);
        assert.equal(actual.mode, expected.mode);
        if (expected.isDirectory()) check(rel);
        else if (expected.isSymbolicLink()) assert.equal(fs.readlinkSync(cleaned), fs.readlinkSync(original));
        else assert.equal(hash(cleaned), hash(original));
      }
    }
    check();
  } finally { fs.rmSync(scratch, { recursive: true, force: true }); }
  console.log('PASS independent GNU tar listing, extracted hashes/modes and no synthetic secret bytes: ' + agent);
}
