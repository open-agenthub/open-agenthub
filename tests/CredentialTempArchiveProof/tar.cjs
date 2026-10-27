#!/usr/bin/env node
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const root = process.env.PROOF_ROOT;
fs.writeFileSync(path.join(root, 'tar-ready'), '');
const deadline = Date.now() + 20000;
const wait = new Int32Array(new SharedArrayBuffer(4));
while (!fs.existsSync(path.join(root, 'credential-ready'))) {
  if (Date.now() > deadline) throw new Error('Credential barrier timeout');
  Atomics.wait(wait, 0, 0, 10);
}
const result = spawnSync('/usr/bin/tar', process.argv.slice(2), { stdio: 'inherit' });
fs.writeFileSync(path.join(root, 'tar-done'), String(result.status));
process.exit(result.status ?? 1);
