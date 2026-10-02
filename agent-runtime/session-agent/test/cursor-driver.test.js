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
    AGENTHUB_STATE_RESTORED: '0', AGENTHUB_HAS_MCP: '0',
    // Unattended sessions default to auto-approve, so that is the baseline the cases below vary.
    AGENTHUB_AUTO_APPROVE: '1', ...overrides
  };
}

test('Cursor driver exposes the provider state contract', () => {
  assert.equal(driver.name, 'Cursor');
  assert.equal(driver.stateDir, '.cursor');
  // Discovered from Cursor Agent CLI 2026.07.23-e383d2b file store (domain "cursor").
  assert.equal(driver.authFilename, 'auth.json');
  assert.deepEqual(driver.attachmentCapabilities, {
    nativeImages: false,
    localImagePaths: true,
    mcpImages: true
  });
  assert.equal(typeof driver.prepare, 'function');
});

test('Cursor interactive starts TUI without resume flags', () => {
  assert.deepEqual(driver.buildCommand(environment(), true), { cmd: 'agent', args: ['--trust'] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), false), { cmd: 'agent', args: ['--trust'] });
});

test('Cursor interactive pre-accepts workspace trust so a started session is not left on a dialog', () => {
  // The TUI asks before doing anything; with nobody at the terminal an API-created session would
  // sit on the question. The autonomous branch has always passed --trust for the same reason.
  for (const env of [
    environment(),
    environment({ AGENTHUB_PROMPT: 'triage the failing build' }),
    environment({
      AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1', AGENTHUB_CLAUDE_SESSION_ID: 'chat-1'
    })
  ]) assert.equal(driver.buildCommand(env, true).args[0], '--trust');
});

test('Cursor interactive session starts on its prompt, except when resuming', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build'
  }), true), { cmd: 'agent', args: ['--trust', 'triage the failing build'] });

  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build',
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'chat-1'
  }), true), { cmd: 'agent', args: ['--trust', '--resume', 'chat-1'] });

  const loginSh = path.join(runtimeDir, 'cursor', 'login.sh');
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_CURSOR_LOGIN: '1', AGENTHUB_PROMPT: 'triage the failing build'
  }), true), { cmd: 'bash', args: [loginSh, '--trust', 'triage the failing build'] });
});

test('Cursor keeps every option ahead of the positional prompt', () => {
  // `[prompt...]` is variadic: an option after it would be swallowed as prompt text instead of
  // parsed, so the prompt has to stay last in both branches.
  for (const args of [
    driver.buildCommand(environment({
      AGENTHUB_PROMPT: 'look at the diff', AGENTHUB_CURSOR_LOGIN: '1'
    }), true).args,
    driver.buildCommand(environment({
      AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'look at the diff', AGENTHUB_HAS_MCP: '1'
    }), true).args
  ]) assert.equal(args.at(-1), 'look at the diff');
});

test('Cursor subscription login runs inside the agent PTY before the interactive TUI', () => {
  const loginSh = path.join(runtimeDir, 'cursor', 'login.sh');
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_CURSOR_LOGIN: '1'
  }), true), { cmd: 'bash', args: [loginSh, '--trust'] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_CURSOR_LOGIN: '1',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'chat-1'
  }), true), { cmd: 'bash', args: [loginSh, '--trust', '--resume', 'chat-1'] });
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: [loginSh, '--trust', '--resume', 'chat-1']
  }), true);
  assert.equal(driver.isResumeCommand({ cmd: 'bash', args: [loginSh, '--trust'] }), false);

  const script = fs.readFileSync(loginSh, 'utf8');
  assert.match(script, /if \[ ! -f "\$\{CURSOR_AUTH_FILE:-\}" \]; then\s+agent login\s+fi/);
  assert.match(script, /^exec agent "\$@"$/m);
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'cursor', 'entrypoint.sh'), 'utf8');
  assert.match(entrypoint, /export AGENTHUB_CURSOR_LOGIN=1/);
  assert.doesNotMatch(entrypoint, /^\s*agent login\s*$/m);
});

test('Cursor resume uses --resume with Claude session id env when chat id present', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'chat-1'
  }), true), { cmd: 'agent', args: ['--trust', '--resume', 'chat-1'] });
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

// The CLI documents --force as "Force allow commands unless explicitly denied", so it is
// auto-approve expressed as a flag. Passing it regardless of the session's setting is what made
// switching auto-approve off on an unattended Cursor session change nothing.
test('Cursor drops --force when the session does not auto-approve', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'fix it', AGENTHUB_AUTO_APPROVE: '0'
  }), true), { cmd: 'agent', args: ['-p', '--trust', 'fix it'] });
  // Absent is not "on": a pod from before the variable existed must not be read as auto-approving.
  const legacy = environment({ AGENTHUB_MODE: 'scheduled', AGENTHUB_PROMPT: 'report' });
  delete legacy.AGENTHUB_AUTO_APPROVE;
  assert.deepEqual(driver.buildCommand(legacy, true),
    { cmd: 'agent', args: ['-p', '--trust', 'report'] });
  // MCP server trust is a startup question, not a per-call approval, so it stays either way.
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'fix it',
    AGENTHUB_AUTO_APPROVE: '0', AGENTHUB_HAS_MCP: '1'
  }), true), { cmd: 'agent', args: ['-p', '--trust', '--approve-mcps', 'fix it'] });
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
  const loginSh = path.join(runtimeDir, 'cursor', 'login.sh');
  assert.equal(driver.isResumeCommand({ cmd: 'agent', args: ['--resume', 'chat-1'] }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'agent', args: ['-p', '--force', '--trust', '--resume', 'chat-1', 'prompt']
  }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: [loginSh, '--trust', '--resume', 'chat-1']
  }), true);
  assert.equal(driver.isResumeCommand({ cmd: 'bash', args: [loginSh, '--trust'] }), false);
  assert.equal(driver.isResumeCommand({ cmd: 'agent', args: ['--trust'] }), false);
  assert.equal(driver.isResumeCommand({ cmd: 'agent', args: ['--resume'] }), false);
  assert.equal(driver.isResumeCommand({ cmd: 'other', args: ['--resume', 'x'] }), false);
  // A login command that is not the runtime's own login.sh must not count as a resume.
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: ['/tmp/evil.sh', '--resume', 'chat-1']
  }), false);
  // An initial prompt sits after the flags, so recognition cannot depend on argument count.
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: [loginSh, '--trust', '--resume', 'chat-1', 'a task']
  }), true);
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
  assert.match(dockerfile, /\/opt\/session-agent\/cursor\/login\.sh/);
  assert.match(dockerfile, /cursor\.com\/install/);
  // The installer only ever serves latest, so the image records the version it got
  // instead of asserting an expected one — a pin assertion here broke the build on
  // every Cursor release. The entrypoint re-exports the recorded value.
  assert.match(dockerfile, /echo "\$AGENT_VER" > \/usr\/local\/share\/cursor-agent\/INSTALLED_VERSION/);
  assert.match(dockerfile, /agent --version 2>&1 \| grep -Fq "\$AGENT_VER"/);
  assert.doesNotMatch(dockerfile, /ARG CURSOR_AGENT_VERSION=/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/node/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/agent/);
  assert.doesNotMatch(dockerfile, /@anthropic-ai|@openai\/codex|COPY claude|COPY codex/);
});
