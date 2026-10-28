'use strict';
const fs = require('node:fs');
const path = require('node:path');
const root = process.env.PROOF_ROOT;
const repo = path.resolve(__dirname, '../..');
if (process.env.PROOF_BASELINE === '1') {
  Object.assign(require('../../agent-runtime/common/credential-install'), require(path.join(root, 'legacy-install.cjs')));
  Object.assign(require('../../agent-runtime/common/state-archive'), require(path.join(root, 'legacy-archive.cjs')));
}
const { createCommonServer } = require('../../agent-runtime/common/server');
const agent = process.env.PROOF_AGENT;
const base = require(path.join(repo, 'agent-runtime', agent, 'driver.js'));
const relativeTarget = agent === 'openclaw' ? '.openclaw/agents/main/agent/auth-profiles.json' : base.stateDir + '/' + base.authFilename;
const target = path.join(process.env.HOME, relativeTarget);
const pausedFs = Object.create(fs);
pausedFs.renameSync = (from, to) => {
  if (to === target) {
    fs.writeFileSync(path.join(root, 'credential-ready'), path.relative(process.env.HOME, from));
    const deadline = Date.now() + 20000;
    const wait = new Int32Array(new SharedArrayBuffer(4));
    // Hold the actual writer immediately before rename until the external tar has read its tree.
    while (!fs.existsSync(path.join(root, 'tar-done'))) {
      if (Date.now() > deadline) throw new Error('Archive barrier timeout');
      Atomics.wait(wait, 0, 0, 10);
    }
  }
  return fs.renameSync(from, to);
};
const dependencies = require('../ClaudeStateCleanupProof/isolated-archive.cjs').dependencies(root, pausedFs);
// Accelerate only the real periodic callback; state-machine ticks and all I/O remain real.
dependencies.setInterval = (fn, ms) => setInterval(fn, ms === 30000 ? 20 : ms);
const driver = { ...base, name: 'Fixture', prepare() {}, validCredential: undefined,
  credentialPath: () => target, installCredential: undefined, reloadsCredentialLive: true,
  inputReadyScreen: undefined, turnHooks: false,
  buildCommand: () => ({ cmd: 'bash', args: ['-c', 'sleep 600'] }) };
const server = createCommonServer({ driver, dependencies });
server.httpServer.once('listening', () => process.send({ type: 'ready', port: server.httpServer.address().port }));
