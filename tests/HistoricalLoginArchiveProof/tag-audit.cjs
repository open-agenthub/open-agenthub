'use strict';
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');
function emit(row) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'login-tag-proof-'));
  try {
    const paths = [row.stateDir + '/' + row.authFilename];
    if (row.agent === 'opencode') paths.push('.opencode/mcp-auth.json');
    if (row.agent === 'openclaw') for (const agent of ['main', 'other'])
      for (const file of ['auth-profiles.json', 'openclaw-agent.sqlite', 'openclaw-agent.sqlite-wal', 'openclaw-agent.sqlite-shm']) paths.push('.openclaw/agents/' + agent + '/agent/' + file);
    for (const name of [...paths, row.stateDir + '/history.jsonl']) {
      fs.mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
      fs.writeFileSync(path.join(root, name), 'synthetic-only');
    }
    const excludes = row.excluded ? [row.stateDir + '/' + row.authFilename, ...row.stateExcludes] : [];
    const archive = path.join(root, 'state.tgz');
    execFileSync('tar', ['czf', archive, '-C', root, ...excludes.map(p => '--exclude=' + p), row.stateDir]);
    const listing = execFileSync('tar', ['tzf', archive], { encoding: 'utf8' }).trim().split('\n');
    for (const name of paths) assert.equal(listing.includes(name), !row.excluded, row.tag + '/' + name);
    assert.ok(listing.includes(row.stateDir + '/history.jsonl'));
    console.log(JSON.stringify({ ...row, realTarVerified: true }));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
}
const git = args => execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
function read(tag, file) { try { return git(['show', `${tag}:${file}`]); } catch { return null; } }
const tags = git(['tag', '--sort=version:refname']).trim().split('\n');
for (const tag of tags) {
  const common = read(tag, 'agent-runtime/common/state-archive.js') || read(tag, 'agent-runtime/common/server.js');
  if (!common) {
    const old = read(tag, 'agent-runtime/session-agent/server.js');
    if (!old || !old.includes('tar czf /tmp/state.tgz') || !old.includes('.claude')) throw new Error('Unrecognized archive: ' + tag);
    emit({ tag, agent: 'claude', stateDir: '.claude', authFilename: '.credentials.json', excluded: false, stateExcludes: [] });
    continue;
  }
  if (!common.includes("driver.stateDir + '/' + driver.authFilename") || !common.includes('excludeArgs')) throw new Error('Unrecognized exclusions: ' + tag);
  for (const agent of ['claude', 'codex', 'cursor', 'openclaw', 'opencode']) {
    const driver = read(tag, `agent-runtime/${agent}/driver.js`);
    if (!driver) continue;
    const stateDir = driver.match(/stateDir:\s*'([^']+)'/)?.[1];
    const authFilename = driver.match(/authFilename:\s*'([^']+)'/)?.[1];
    if (!stateDir || !authFilename) throw new Error('Unrecognized driver: ' + tag + '/' + agent);
    const array = driver.match(/stateExcludes:\s*(\[[\s\S]*?\])/);
    const text = array ? array[1].replace(/\/\/[^\n]*/g, '') : '[]';
    const stateExcludes = [...text.matchAll(/'([A-Za-z0-9._/*-]+)'/g)].map(match => match[1]);
    if (text.replace(/'([A-Za-z0-9._/*-]+)'/g, '').replace(/[\s,\[\]]/g, '')) throw new Error('Unrecognized exclude array: ' + tag);
    emit({ tag, agent, stateDir, authFilename, excluded: true, stateExcludes });
  }
}
