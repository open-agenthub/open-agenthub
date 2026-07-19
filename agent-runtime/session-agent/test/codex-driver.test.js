'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const driver = require('../../codex/driver');

function environment(overrides = {}) {
  return {
    AGENTHUB_MODE: 'interactive', AGENTHUB_PROMPT: '', AGENTHUB_RESUME: '0',
    AGENTHUB_STATE_RESTORED: '0', ...overrides
  };
}

test('Codex driver exposes the provider state contract', () => {
  assert.equal(driver.name, 'Codex');
  assert.equal(driver.stateDir, '.codex');
  assert.equal(driver.authFilename, 'auth.json');
  assert.equal(typeof driver.prepare, 'function');
});

test('Codex interactive fresh and restored commands use only explicit resume shapes', () => {
  assert.deepEqual(driver.buildCommand(environment(), true), { cmd: 'codex', args: [] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), true), { cmd: 'codex', args: ['resume', '--last'] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), false), { cmd: 'codex', args: [] });
});

test('Codex autonomous and scheduled commands use pinned exec flags and valid resume ordering', () => {
  const flags = ['--sandbox', 'workspace-write', '--json', '--dangerously-bypass-hook-trust'];
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'fix it'
  }), true), { cmd: 'codex', args: ['exec', ...flags, 'fix it'] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'scheduled', AGENTHUB_PROMPT: 'report',
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), true), { cmd: 'codex', args: ['exec', ...flags, 'resume', '--last', 'report'] });
});

test('Codex resume recognition rejects fresh and merely resume-like commands', () => {
  assert.equal(driver.isResumeCommand({ cmd: 'codex', args: ['resume', '--last'] }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'codex', args: ['exec', '--sandbox', 'workspace-write', '--json',
      '--dangerously-bypass-hook-trust', 'resume', '--last', 'prompt']
  }), true);
  assert.equal(driver.isResumeCommand({ cmd: 'codex', args: ['exec', 'resume later'] }), false);
  assert.equal(driver.isResumeCommand({ cmd: 'other', args: ['resume', '--last'] }), false);
  assert.equal(driver.isResumeCommand({ cmd: 'codex', args: [] }), false);
});

test('Codex missing-resume fallback requires representative missing-state output', () => {
  assert.equal(driver.isMissingResume('No saved session found to resume', 1, 20_000), true);
  assert.equal(driver.isMissingResume('No session found with id abc', 1, 20_000), true);
  assert.equal(driver.isMissingResume('network failure', 1, 1), false);
  assert.equal(driver.isMissingResume('No saved session found to resume', 0, 1), false);
});

test('Codex prepare scopes CODEX_API_KEY to autonomous child environment only', () => {
  const env = environment({ AGENTHUB_MODE: 'autonomous', CODEX_API_KEY: 'synthetic-key' });
  const result = driver.prepare(env);
  assert.equal(env.CODEX_API_KEY, undefined);
  assert.deepEqual(result, { childEnv: { CODEX_API_KEY: 'synthetic-key' } });

  const interactive = environment({ CODEX_API_KEY: 'already-used-by-entrypoint' });
  assert.equal(driver.prepare(interactive), undefined);
  assert.equal(interactive.CODEX_API_KEY, undefined);
});

test('Codex entrypoint owns config, auth mode, watcher, and stale-auth ordering', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'codex', 'entrypoint.sh'), 'utf8');
  assert.match(entrypoint, /export AGENTHUB_STATE_DIR=\.codex/);
  assert.match(entrypoint, /source "\$COMMON_ENTRYPOINT"/);
  assert.match(entrypoint, /CODEX_HOME="\$\{CODEX_HOME:-\$HOME\/\.codex\}"/);
  assert.match(entrypoint, /cli_auth_credentials_store = "file"/);
  assert.match(entrypoint, /rm -f "\$CODEX_HOME\/auth\.json"/);
  assert.match(entrypoint, /\/secrets\/codex\/auth\.json/);
  assert.match(entrypoint, /chmod 600 "\$CODEX_HOME\/auth\.json"/);
  assert.match(entrypoint, /printf '%s\\n' "\$CODEX_API_KEY" \| codex login --with-api-key/);
  assert.match(entrypoint, /unset CODEX_API_KEY/);
  assert.match(entrypoint, /codex login --device-auth/);
  assert.match(entrypoint, /AGENTHUB_AUTH_MODE/);
  assert.match(entrypoint, /subscription\)/);
  assert.match(entrypoint, /apikey\)/);
  assert.match(entrypoint, /CODEX_API_KEY is required/);
  assert.match(entrypoint, /auth-watcher\.js/);
  assert.match(entrypoint, /AGENTHUB_CODEX_AUTH_EXPECT_CREATE/);
  assert.match(entrypoint, /mcp-config\.js/);
  assert.match(entrypoint, /AGENTHUB_DRIVER="\$RUNTIME\/codex\/driver\.js"/);
  assert.ok(entrypoint.indexOf('source "$COMMON_ENTRYPOINT"') < entrypoint.indexOf('rm -f "$CODEX_HOME/auth.json"'));
  assert.ok(entrypoint.indexOf('rm -f "$CODEX_HOME/auth.json"') < entrypoint.indexOf('/secrets/codex/auth.json'));
});

test('Codex image pins CLI and preserves custom-image injection paths', () => {
  const dockerfile = fs.readFileSync(path.join(runtimeDir, 'codex', 'Dockerfile'), 'utf8');
  assert.match(dockerfile, /COPY common\s+\/opt\/session-agent\/common/);
  assert.match(dockerfile, /COPY codex\s+\/opt\/session-agent\/codex/);
  assert.match(dockerfile, /COPY codex\/entrypoint\.sh\s+\/usr\/local\/bin\/entrypoint\.sh/);
  assert.match(dockerfile, /npm install -g @openai\/codex@0\.144\.5/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/node/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/codex/);
  assert.doesNotMatch(dockerfile, /@anthropic-ai/);
});
