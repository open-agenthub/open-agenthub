// A real filesystem can expose the same coarse ctime for two changes. Permission mode
// belongs in the archive signature directly, rather than relying on ctime to imply it.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { signatureScript } = require('../../agent-runtime/common/state-archive.js');
const execute = promisify(execFile);
const root = await fs.mkdtemp(path.join(os.tmpdir(), 'archive-mode-proof-'));
const state = path.join(root, '.proof-state');
await fs.mkdir(state);
const file = path.join(state, 'native-state');
await fs.writeFile(file, 'native state');
const current = signatureScript({ stateDir: '.proof-state', authFilename: 'auth.json' });
// Retain the production find traversal and ordering, observing its records before the
// final digest so the old and current digests use the exact same filesystem snapshot.
const records = async () => (await execute('/bin/sh', ['-c', current.replace(/\| sha256sum$/, '')], { cwd: root })).stdout.trimEnd().split('\n');
const digest = lines => crypto.createHash('sha256').update([...lines].sort().join('\n') + '\n').digest('hex');
const withoutMode = lines => lines.map(line => line.replace(/^[0-7]{3,4} /, ''));
let reproduced = false;
try {
 for (let attempt = 0; attempt < 500; attempt++) {
  await fs.chmod(file, 0o644);
  const before = await records();
  const baseline = await fs.stat(file);
  await fs.chmod(file, 0o600);
  const after = await records();
  const changed = await fs.stat(file);
  if (digest(withoutMode(before)) !== digest(withoutMode(after))) continue;
  assert.notEqual(baseline.mode, changed.mode);
  assert.equal(baseline.ctimeMs, changed.ctimeMs);
  assert.notEqual(digest(before), digest(after), 'production archive signature must distinguish different modes with identical ctime');
  console.log('PASS: real distinct modes with identical ctime; the exact same production metadata snapshots collide without mode and differ with it.');
  reproduced = true; break;
 }
 assert(reproduced, 'No coarse-time collision reproduced on this filesystem; do not claim the negative control passed.');
} finally { await fs.rm(root, { recursive: true, force: true }); }
