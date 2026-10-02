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

test('git credentials reach the pod only as host-bound store entries', () => {
  const fs = require('node:fs');
  const entrypoint = fs.readFileSync(scriptPath, 'utf8');

  // The store is the single credential source and each entry names one host.
  assert.match(entrypoint, /git config --global credential\.helper store/);

  // Verified with git 2.47.3: the helper this replaced was registered globally, so
  // `git credential fill` for any host — including one an injected prompt chose — answered with
  // the user's token. The store returns nothing for a host it has no entry for.
  assert.doesNotMatch(entrypoint, /credential\.helper '!f\(\)/);
  assert.doesNotMatch(entrypoint, /gitlab_token/);
  assert.doesNotMatch(entrypoint, /github_token/);

  // setup-cli-auth.sh derives gh/glab config from the same store, which is what made the
  // GITLAB_TOKEN export unnecessary — and keeps the token out of the session environment.
  assert.match(entrypoint, /setup-cli-auth\.sh/);
  assert.doesNotMatch(entrypoint, /export GITLAB_TOKEN/);
});
