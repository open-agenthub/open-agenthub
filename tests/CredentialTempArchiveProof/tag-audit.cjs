'use strict';
const { execFileSync } = require('node:child_process');
const git = args => execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
function read(tag, file) { try { return git(['show', `${tag}:${file}`]); } catch { return null; } }
for (const tag of git(['tag', '--sort=version:refname']).trim().split('\n')) {
  const writer = read(tag, 'agent-runtime/common/credential-install.js');
  if (!writer) {
    const server = read(tag, 'agent-runtime/common/server.js') || read(tag, 'agent-runtime/session-agent/server.js');
    if (!server || server.includes('writeCredentialFile')) throw new Error('Unrecognized predecessor: ' + tag);
    console.log(JSON.stringify({ tag, sharedTemporaryWriter: false }));
    continue;
  }
  if (!writer.includes("path.join(directory, '.' + path.basename(target) + '.' + process.pid + '.tmp')") ||
      !writer.includes('fsImpl.writeFileSync(temporary, body') || !writer.includes('fsImpl.renameSync(temporary, target)'))
    throw new Error('Unrecognized temporary writer: ' + tag);
  const server = read(tag, 'agent-runtime/common/server.js');
  if (!server?.includes('credentials.writeCredentialFile(target, body, fs)')) throw new Error('Unrecognized writer call: ' + tag);
  const agents = [];
  for (const agent of ['claude', 'codex', 'cursor', 'openclaw', 'opencode']) {
    const driver = read(tag, `agent-runtime/${agent}/driver.js`);
    if (!driver) continue;
    if (driver.includes('function installCredential') && !driver.includes('writeCredentialFile(target, body, fsImpl)'))
      throw new Error('Unrecognized provider writer: ' + tag + '/' + agent);
    agents.push(agent);
  }
  console.log(JSON.stringify({ tag, sharedTemporaryWriter: true, agents }));
}
