'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const root = process.argv[2];
const old = path.join(root, 'old.tgz'), clean = path.join(root, 'cleaned.tgz');
const list = file => execFileSync('tar', ['tvzf', file], { encoding: 'utf8' }).trim().split('\n');
assert.deepEqual(list(clean), list(old).filter(line => !line.includes('.claude/backups')));
const source = fs.readFileSync(path.join(__dirname, '../../agent-runtime/common/entrypoint-common.sh'), 'utf8');
const command = 'tar xzf /tmp/state.tgz --exclude=".claude/backups" --exclude="./.claude/backups" -C "$HOME" 2>/dev/null';
assert.ok(source.includes(command + '; then'));
const restore = path.join(root, 'restored');
fs.mkdirSync(restore, { recursive: true });
execFileSync('tar', ['xzf', old, '--exclude=.claude/backups', '--exclude=./.claude/backups', '-C', restore]);
assert.ok(!fs.existsSync(path.join(restore, '.claude/backups')));
function hashFile(file) {
  // Hash an opened regular file so a replaced symlink cannot redirect the read after lstat.
  const fd = fs.openSync(file, fs.constants.O_RDONLY | fs.constants.O_NOFOLLOW);
  try {
    assert.ok(fs.fstatSync(fd).isFile());
    return crypto.hash('sha256', fs.readFileSync(fd));
  } finally { fs.closeSync(fd); }
}
function check(dir, relative = '') {
  for (const name of fs.readdirSync(dir)) {
    const rel = path.join(relative, name);
    if (rel === '.claude/backups') continue;
    const original = path.join(root, 'home', rel), restored = path.join(restore, rel);
    const stat = fs.lstatSync(original), actual = fs.lstatSync(restored);
    assert.equal(actual.mode, stat.mode);
    if (stat.isDirectory()) check(original, rel);
    else if (stat.isSymbolicLink()) assert.equal(fs.readlinkSync(restored), fs.readlinkSync(original));
    else assert.equal(hashFile(restored), hashFile(original));
  }
}
check(path.join(root, 'home'));
console.log('PASS GNU tar verbose listing identical except backups; real entrypoint restore drops old backups and preserves file hashes, modes and symlinks');
console.log(list(clean).join('\n'));
