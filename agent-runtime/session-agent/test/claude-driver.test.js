'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const driver = require('../../claude/driver');

function environment(overrides = {}) {
  return {
    AGENTHUB_MODE: 'interactive',
    AGENTHUB_PROMPT: '',
    AGENTHUB_ALLOWED_TOOLS: '',
    AGENTHUB_HAS_MCP: '0',
    AGENTHUB_RESUME: '0',
    AGENTHUB_CLAUDE_SESSION_ID: '',
    AGENTHUB_STATE_RESTORED: '0',
    ...overrides
  };
}

test('Claude driver exposes its state and subscription-auth contract', () => {
  assert.equal(driver.name, 'Claude');
  assert.equal(driver.stateDir, '.claude');
  assert.equal(driver.authFilename, '.credentials.json');
  assert.equal(typeof driver.prepare, 'function');
});

test('Claude interactive fresh command retains fixed session id', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude', args: ['--session-id', 'fixed-session']
  });
});

test('Claude resume command requires requested resume, restored state, and fixed id', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude', args: ['--resume', 'fixed-session']
  });

  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), false), {
    cmd: 'claude', args: ['--session-id', 'fixed-session']
  });
});

test('Claude resume falls back for the same output and quick-exit conditions', () => {
  assert.equal(driver.isMissingResume('No conversation found for session', 1, 15_000), true);
  assert.equal(driver.isMissingResume('unexpected failure', 1, 9_999), true);
  assert.equal(driver.isMissingResume('unexpected failure', 1, 10_000), false);
  assert.equal(driver.isMissingResume('No conversation found for session', 0, 1), false);
});

test('Claude autonomous command retains prompt permission mode and allowlist', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_PROMPT: 'fix it',
    AGENTHUB_ALLOWED_TOOLS: 'Read, Edit'
  }), true), {
    cmd: 'claude',
    args: ['-p', 'fix it', '--permission-mode', 'acceptEdits', '--allowedTools', 'Read,Edit']
  });
});

test('Claude scheduled command and MCP config retain current ordering', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'scheduled',
    AGENTHUB_PROMPT: 'report',
    AGENTHUB_HAS_MCP: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude',
    args: ['--mcp-config', '/secrets/mcp/mcp.json', '--session-id', 'fixed-session',
      '-p', 'report', '--permission-mode', 'acceptEdits']
  });
});

test('Claude entrypoint keeps auth restore and watcher provider-specific', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'claude', 'entrypoint.sh'), 'utf8');

  assert.match(entrypoint, /source "\$COMMON_ENTRYPOINT"/);
  assert.match(entrypoint, /\/secrets\/claude\/credentials\.json/);
  assert.match(entrypoint, /\$HOME\/\.claude\/\.credentials\.json/);
  assert.match(entrypoint, /\/claude-credentials/);
  assert.match(entrypoint, /claude\/hooks\/mcp-policy-hook\.sh/);
  assert.match(entrypoint, /AGENTHUB_DRIVER="\$RUNTIME\/claude\/driver\.js"/);
  assert.match(entrypoint, /exec node "\$RUNTIME\/common\/server\.js"/);
  assert.ok(entrypoint.indexOf('source "$COMMON_ENTRYPOINT"') <
    entrypoint.indexOf('/secrets/claude/credentials.json'));
});

test('Claude image preserves runtime and custom-image injection paths', () => {
  const dockerfile = fs.readFileSync(path.join(runtimeDir, 'claude', 'Dockerfile'), 'utf8');

  assert.match(dockerfile, /COPY common\s+\/opt\/session-agent\/common/);
  assert.match(dockerfile, /COPY claude\s+\/opt\/session-agent\/claude/);
  assert.match(dockerfile, /COPY claude\/entrypoint\.sh\s+\/usr\/local\/bin\/entrypoint\.sh/);
  assert.match(dockerfile, /npm install -g @anthropic-ai\/claude-code/);
  assert.match(dockerfile, /\/usr\/local\/bin\/claude/);
});
