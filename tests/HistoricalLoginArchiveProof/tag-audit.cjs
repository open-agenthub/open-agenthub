'use strict';
const { execFileSync } = require('node:child_process');
const vm = require('node:vm');
const git = args => execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
function read(tag, file) { try { return git(['show', `${tag}:${file}`]); } catch { return null; } }
const tags = git(['tag', '--sort=version:refname']).trim().split('\n');
for (const tag of tags) {
  const common = read(tag, 'agent-runtime/common/state-archive.js') || read(tag, 'agent-runtime/common/server.js');
  if (!common) {
    const old = read(tag, 'agent-runtime/session-agent/server.js');
    if (!old || !old.includes('tar czf /tmp/state.tgz') || !old.includes('.claude')) throw new Error('Unrecognized archive: ' + tag);
    console.log(JSON.stringify({ tag, agent: 'claude', stateDir: '.claude', authFilename: '.credentials.json', excluded: false, stateExcludes: [] }));
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
    const stateExcludes = array ? vm.runInNewContext(array[1], {}, { timeout: 1000 }) : [];
    console.log(JSON.stringify({ tag, agent, stateDir, authFilename, excluded: true, stateExcludes }));
  }
}
