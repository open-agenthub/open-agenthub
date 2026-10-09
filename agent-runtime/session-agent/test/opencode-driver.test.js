'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const driver = require('../../opencode/driver');
const { validateDriver } = require('../../common/driver-contract');

function environment(overrides = {}) {
  return {
    AGENTHUB_MODE: 'interactive', AGENTHUB_PROMPT: '', AGENTHUB_RESUME: '0',
    AGENTHUB_STATE_RESTORED: '0', ...overrides
  };
}

test('OpenCode driver satisfies the provider state contract', () => {
  assert.equal(validateDriver(driver), driver);
  assert.equal(driver.name, 'OpenCode');
  assert.equal(driver.stateDir, '.opencode');
  // Pinned from OpenCode 1.18.34: $XDG_DATA_HOME/opencode/auth.json, linked to ~/.opencode.
  assert.equal(driver.authFilename, 'auth.json');
  assert.deepEqual(driver.stateExcludes, ['.opencode/mcp-auth.json', '.opencode/log', '.opencode/bin']);
});

test('prepare scopes OPENCODE_API_KEY to the agent child', () => {
  const env = environment({ OPENCODE_API_KEY: 'secret' });
  assert.deepEqual(driver.prepare(env), { childEnv: { OPENCODE_API_KEY: 'secret' } });
  assert.equal(env.OPENCODE_API_KEY, undefined);
  assert.equal(driver.prepare(environment()), undefined);
});

test('interactive start hands the prompt to the TUI', () => {
  assert.deepEqual(driver.buildCommand(environment(), true), { cmd: 'opencode', args: [] });
  assert.deepEqual(driver.buildCommand(environment({ AGENTHUB_PROMPT: 'fix it' }), true),
    { cmd: 'opencode', args: ['--prompt', 'fix it'] });
});

test('interactive resume continues the last session without replaying the prompt', () => {
  const command = driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'fix it', AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), true);
  assert.deepEqual(command, { cmd: 'opencode', args: ['--continue'] });
  assert.equal(driver.isResumeCommand(command), true);
});

test('resume needs restored state and permission to resume', () => {
  const env = environment({ AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '0' });
  assert.deepEqual(driver.buildCommand(env, true).args, []);
  const restored = environment({ AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1' });
  assert.deepEqual(driver.buildCommand(restored, false).args, []);
});

test('subscription login runs in the PTY before the TUI', () => {
  const command = driver.buildCommand(environment({ AGENTHUB_OPENCODE_LOGIN: '1', AGENTHUB_PROMPT: 'go' }), true);
  assert.equal(command.cmd, 'bash');
  assert.deepEqual(command.args, [path.join(__dirname, '..', '..', 'opencode', 'login.sh'), '--prompt', 'go']);
  assert.equal(driver.isResumeCommand(command), false);
});

test('unattended modes use opencode run without bypassing the policy plugin', () => {
  for (const mode of ['autonomous', 'scheduled']) {
    const command = driver.buildCommand(environment({ AGENTHUB_MODE: mode, AGENTHUB_PROMPT: 'task' }), true);
    assert.deepEqual(command, { cmd: 'opencode', args: ['run', 'task'] });
    // --auto (and its aliases) would approve whatever the plugin let through *and* anything it
    // never saw; approval belongs to the plugin alone.
    assert.ok(!command.args.some(arg => /auto|yolo|skip-permissions/.test(arg)));
  }
  const resumed = driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'task', AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), true);
  assert.deepEqual(resumed.args, ['run', '--continue', 'task']);
  assert.equal(driver.isResumeCommand(resumed), true);
});

test('only an explicit missing session counts as a failed resume', () => {
  assert.equal(driver.isMissingResume('Error: Session not found', 1), true);
  assert.equal(driver.isMissingResume('Error: Session not found', 0), false);
  assert.equal(driver.isMissingResume('Invalid API key.', 1), false);
});
