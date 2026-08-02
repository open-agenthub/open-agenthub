'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const bashPath = process.platform === 'win32'
  ? 'C:\\Program Files\\Git\\bin\\bash.exe'
  : 'bash';
const scriptPath = path.join(__dirname, '..', '..', 'common', 'entrypoint-common.sh');

function toBashPath(file) {
  if (process.platform !== 'win32') return file;
  return file.replace(/^([A-Za-z]):\\/, (_, drive) => `/${drive.toLowerCase()}/`).replace(/\\/g, '/');
}

test('shared runtime entrypoint is valid Bash', () => {
  const result = spawnSync(bashPath, ['-n', toBashPath(scriptPath)], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
});
