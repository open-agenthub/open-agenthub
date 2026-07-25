'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const driver = require('../../cursor/driver');

function environment(overrides = {}) {
  return {
    AGENTHUB_MODE: 'interactive', AGENTHUB_PROMPT: '', AGENTHUB_RESUME: '0',
    AGENTHUB_STATE_RESTORED: '0', AGENTHUB_HAS_MCP: '0', ...overrides
  };
}

test('Cursor driver exposes the provider state contract', () => {
  assert.equal(driver.name, 'Cursor');
  assert.equal(driver.stateDir, '.cursor');
  // Discovered from Cursor Agent CLI 2026.07.23-e383d2b file store (domain "cursor").
  assert.equal(driver.authFilename, 'auth.json');
  assert.equal(typeof driver.prepare, 'function');
});

test('Cursor interactive starts TUI without resume flags', () => {
  assert.deepEqual(driver.buildCommand(environment(), true), { cmd: 'agent', args: [] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), false), { cmd: 'agent', args: [] });
});

test('Cursor subscription login runs inside the agent PTY via driver flag', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_CURSOR_LOGIN: '1'
  }), true), { cmd: 'agent', args: ['login'] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_CURSOR_LOGIN: '1',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'chat-1'
  }), true), { cmd: 'agent', args: ['login'] });
  assert.equal(driver.isResumeCommand({ cmd: 'agent', args: ['login'] }), false);

  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'cursor', 'entrypoint.sh'), 'utf8');
  assert.match(entrypoint, /export AGENTHUB_CURSOR_LOGIN=1/);
  assert.doesNotMatch(entrypoint, /^\s*agent login\s*$/m);
});

test('Cursor resume uses --resume with Claude session id env when chat id present', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'chat-1'
  }), true), { cmd: 'agent', args: ['--resume', 'chat-1'] });
});

test('Cursor autonomous uses print, force, and trust', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'fix it'
  }), true), { cmd: 'agent', args: ['-p', '--force', '--trust', 'fix it'] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'scheduled', AGENTHUB_PROMPT: 'report',
    AGENTHUB_HAS_MCP: '1'
  }), true), {
    cmd: 'agent',
    args: ['-p', '--force', '--trust', '--approve-mcps', 'report']
  });
});

test('Cursor autonomous resume keeps print flags before --resume', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_PROMPT: 'continue',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'chat-9'
  }), true), {
    cmd: 'agent',
    args: ['-p', '--force', '--trust', '--resume', 'chat-9', 'continue']
  });
});

test('Cursor resume recognition rejects fresh and merely resume-like commands', () => {
  assert.equal(driver.isResumeCommand({ cmd: 'agent', args: ['--resume', 'chat-1'] }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'agent', args: ['-p', '--force', '--trust', '--resume', 'chat-1', 'prompt']
  }), true);
  assert.equal(driver.isResumeCommand({ cmd: 'agent', args: [] }), false);
  assert.equal(driver.isResumeCommand({ cmd: 'agent', args: ['--resume'] }), false);
  assert.equal(driver.isResumeCommand({ cmd: 'other', args: ['--resume', 'x'] }), false);
});

test('Cursor missing-resume fallback requires representative missing-chat output', () => {
  assert.equal(driver.isMissingResume('No conversation found with id chat-1', 1), true);
  assert.equal(driver.isMissingResume('Chat not found', 1), true);
  assert.equal(driver.isMissingResume('network failure', 1), false);
  assert.equal(driver.isMissingResume('No conversation found', 0), false);
});

test('Cursor prepare scopes CURSOR_API_KEY to the agent child for every mode', () => {
  const autonomous = environment({ AGENTHUB_MODE: 'autonomous', CURSOR_API_KEY: 'synthetic-key' });
  assert.deepEqual(driver.prepare(autonomous), { childEnv: { CURSOR_API_KEY: 'synthetic-key' } });
  assert.equal(autonomous.CURSOR_API_KEY, undefined);

  const interactive = environment({ CURSOR_API_KEY: 'synthetic-interactive-key' });
  assert.deepEqual(driver.prepare(interactive), { childEnv: { CURSOR_API_KEY: 'synthetic-interactive-key' } });
  assert.equal(interactive.CURSOR_API_KEY, undefined);
});

test('Cursor entrypoint owns config, auth mode, watcher, and stale-auth ordering', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'cursor', 'entrypoint.sh'), 'utf8');
  assert.match(entrypoint, /export AGENTHUB_STATE_DIR=\.cursor/);
  assert.match(entrypoint, /source "\$COMMON_ENTRYPOINT"/);
  assert.match(entrypoint, /CURSOR_CONFIG_DIR=/);
  assert.match(entrypoint, /AGENT_CLI_CREDENTIAL_STORE=file/);
  assert.match(entrypoint, /NO_OPEN_BROWSER=1/);
  assert.match(entrypoint, /cli-config\.js/);
  assert.match(entrypoint, /cli-config\.json/);
  assert.match(entrypoint, /mcp-config\.js/);
  assert.match(entrypoint, /rm -f "\$CURSOR_AUTH_FILE"/);
  assert.match(entrypoint, /\/secrets\/cursor\/auth\.json/);
  assert.match(entrypoint, /auth-watcher\.js/);
  assert.match(entrypoint, /AGENTHUB_CURSOR_AUTH_EXPECT_CREATE/);
  assert.match(entrypoint, /AGENTHUB_CURSOR_AUTH_BASELINE_SHA256/);
  assert.match(entrypoint, /AGENTHUB_CURSOR_LOGIN/);
  assert.match(entrypoint, /CURSOR_API_KEY/);
  assert.match(entrypoint, /subscription\)/);
  assert.match(entrypoint, /apikey\)/);
  assert.match(entrypoint, /AGENTHUB_DRIVER="\$RUNTIME\/cursor\/driver\.js"/);
  assert.ok(entrypoint.indexOf('source "$COMMON_ENTRYPOINT"') <
    entrypoint.indexOf('rm -f "$CURSOR_AUTH_FILE"'));
  assert.ok(entrypoint.indexOf('rm -f "$CURSOR_AUTH_FILE"') <
    entrypoint.indexOf('/secrets/cursor/auth.json'));
});

test('Cursor image installs agent CLI and preserves custom-image injection paths', () => {
  const dockerfile = fs.readFileSync(path.join(runtimeDir, 'cursor', 'Dockerfile'), 'utf8');
  assert.match(dockerfile, /COPY common\s+\/opt\/session-agent\/common/);
  assert.match(dockerfile, /COPY cursor\s+\/opt\/session-agent\/cursor/);
  assert.match(dockerfile, /COPY cursor\/entrypoint\.sh\s+\/usr\/local\/bin\/entrypoint\.sh/);
  assert.match(dockerfile, /cursor\.com\/install/);
  assert.match(dockerfile, /ARG CURSOR_AGENT_VERSION=/);
  assert.match(dockerfile, /test "\$AGENT_VER" = "\$CURSOR_AGENT_VERSION"/);
  assert.match(dockerfile, /agent --version 2>&1 \| grep -Fq "\$CURSOR_AGENT_VERSION"/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/node/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/agent/);
  assert.doesNotMatch(dockerfile, /@anthropic-ai|@openai\/codex|COPY claude|COPY codex/);
});
