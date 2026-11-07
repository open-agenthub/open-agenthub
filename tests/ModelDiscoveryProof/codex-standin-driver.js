'use strict';
// The real Codex driver with one change: the interactive process is a stand-in that stays up.
// The pinned TUI exits at bootstrap against the mock (it asks routes the mock does not answer),
// and an exited agent refuses credential installs. Everything under test — credentialPath,
// validCredential, installCredential, modelCatalog — is the real driver's.
const path = require('node:path');
const real = require(path.join(process.env.AGENTHUB_PROOF_WORKTREE, 'agent-runtime/codex/driver.js'));
module.exports = { ...real, buildCommand: () => ({ cmd: process.execPath, args: ['-e', 'setInterval(() => {}, 1 << 30)'] }) };
