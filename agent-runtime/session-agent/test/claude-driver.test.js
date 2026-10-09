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
  assert.deepEqual(driver.attachmentCapabilities, {
    nativeImages: false,
    localImagePaths: true,
    mcpImages: true
  });
  assert.equal(typeof driver.prepare, 'function');
});

test('Claude prepare scopes the API key to the provider child and leaves subscription mode unchanged', () => {
  const apiKeyEnv = environment({ ANTHROPIC_API_KEY: 'synthetic-claude-key' });

  assert.deepEqual(driver.prepare(apiKeyEnv), {
    childEnv: { ANTHROPIC_API_KEY: 'synthetic-claude-key' }
  });
  assert.equal(apiKeyEnv.ANTHROPIC_API_KEY, undefined);

  const subscriptionEnv = environment();
  assert.equal(driver.prepare(subscriptionEnv), undefined);
  assert.equal(subscriptionEnv.ANTHROPIC_API_KEY, undefined);
});

test('Claude interactive fresh command retains fixed session id', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude', args: ['--session-id', 'fixed-session']
  });
});

test('Claude interactive session starts on its prompt and stays a live REPL', () => {
  // The positional prompt is what makes an API-created session already be working when a person
  // takes it over; -p would answer once and exit, leaving nothing to take over.
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude', args: ['--session-id', 'fixed-session', 'triage the failing build']
  });

  const bare = driver.buildCommand(environment({ AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session' }), true);
  assert.deepEqual(bare.args, ['--session-id', 'fixed-session']);
  assert.ok(!bare.args.includes('-p'));
});

test('Claude appends a caller system prompt in every mode without replacing its own', () => {
  const interactive = driver.buildCommand(environment({
    AGENTHUB_SYSTEM_PROMPT: 'You review, you do not commit.',
    AGENTHUB_PROMPT: 'look at the diff'
  }), true);
  assert.deepEqual(interactive.args,
    ['--append-system-prompt', 'You review, you do not commit.', 'look at the diff']);
  // --system-prompt would drop Claude Code's own instructions along with its tool guidance.
  assert.ok(!interactive.args.includes('--system-prompt'));

  const chat = driver.buildCommand(environment({
    AGENTHUB_UI_MODE: 'chat', AGENTHUB_SYSTEM_PROMPT: 'be terse'
  }), true);
  assert.deepEqual(chat.args.slice(0, 2), ['--append-system-prompt', 'be terse']);

  const autonomous = driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous', AGENTHUB_SYSTEM_PROMPT: 'be terse', AGENTHUB_PROMPT: 'fix it'
  }), true);
  assert.deepEqual(autonomous.args,
    ['--append-system-prompt', 'be terse', '-p', 'fix it', '--permission-mode', 'acceptEdits']);
});

test('Claude does not re-submit the prompt when resuming an interactive session', () => {
  // The restored conversation already contains the task; repeating it would start the work over.
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude', args: ['--resume', 'fixed-session']
  });

  // A resume that was requested but has no restored state is a fresh start, so the prompt applies.
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '0',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true).args, ['--session-id', 'fixed-session', 'triage the failing build']);
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

test('Claude names its transcript from the fixed session id and the cwd slug', () => {
  const home = '/home/agent';
  const expected = path.join(home, '.claude', 'projects', '-workspace-repo', 'fixed-session.jsonl');
  const seen = [];
  const fakeFs = { existsSync(file) { seen.push(file); return file === expected; } };

  assert.equal(driver.findTranscript({
    env: environment({ AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session' }),
    home, cwd: '/workspace/repo', fs: fakeFs
  }), expected);
  assert.deepEqual(seen, [expected]);

  // Not written yet (first turn pending): asked again later rather than guessed.
  assert.equal(driver.findTranscript({
    env: environment({ AGENTHUB_CLAUDE_SESSION_ID: 'other' }), home, cwd: '/workspace/repo', fs: fakeFs
  }), null);
  // Without a fixed id there is nothing to look for.
  assert.equal(driver.findTranscript({ env: environment(), home, cwd: '/workspace/repo', fs: fakeFs }), null);
});

test('Claude resume falls back only on the CLI saying the conversation is gone', () => {
  assert.equal(driver.isMissingResume('No conversation found with session ID: abc', 1, 15_000), true);
  assert.equal(driver.isMissingResume('No conversation found to continue', 1, 500), true);
  assert.equal(driver.isMissingResume('No conversation found for session', 0, 1), false);
});

test('Claude keeps --resume through a fast crash that is not about the conversation', () => {
  // An expired login or an unreachable API dies within a second or two; that used to count as
  // "no saved conversation" and the next launch dropped --resume along with the history.
  assert.equal(driver.isMissingResume('Invalid API key · Please run /login', 1, 1_200), false);
  assert.equal(driver.isMissingResume('fetch failed: ECONNREFUSED', 1, 300), false);
  assert.equal(driver.isMissingResume('', 1, 0), false);
});

test('Claude chat command streams JSON on both ends through a pipe', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_UI_MODE: 'chat',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude',
    args: ['--session-id', 'fixed-session', '-p', '--input-format', 'stream-json',
      '--output-format', 'stream-json', '--include-partial-messages', '--verbose'],
    pipe: true
  });
});

test('Claude chat command keeps resume and MCP config handling', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_UI_MODE: 'chat',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_HAS_MCP: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'fixed-session'
  }), true), {
    cmd: 'claude',
    args: ['--mcp-config', '/secrets/mcp/mcp.json', '--resume', 'fixed-session',
      '-p', '--input-format', 'stream-json', '--output-format', 'stream-json',
      '--include-partial-messages', '--verbose'],
    pipe: true
  });
});

test('Claude chat ui mode only applies to interactive sessions', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_UI_MODE: 'chat',
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_PROMPT: 'fix it'
  }), true), {
    cmd: 'claude',
    args: ['-p', 'fix it', '--permission-mode', 'acceptEdits']
  });
});

test('Claude autonomous command retains prompt permission mode and allowlist', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_PROMPT: 'fix it',
    AGENTHUB_ALLOWED_TOOLS: '["Read","Edit"]'
  }), true), {
    cmd: 'claude',
    args: ['-p', 'fix it', '--permission-mode', 'acceptEdits', '--allowedTools', 'Read,Edit']
  });
});

test('Claude automation translates MCP rules and exact shell commands into native allowed tools', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_PROMPT: 'fix it',
    AGENTHUB_ALLOWED_TOOLS: '["Read","Edit","Read"]',
    AGENTHUB_ALLOWED_MCP_TOOLS: '["mcp__docs__search","mcp__git__*"]',
    AGENTHUB_ALLOWED_COMMANDS: '["git status","npm test"]'
  }), true), {
    cmd: 'claude',
    args: ['-p', 'fix it', '--permission-mode', 'acceptEdits', '--allowedTools',
      'Read,Edit,mcp__docs__search,mcp__git__*,Bash(git status),Bash(npm test)']
  });
});

test('Claude built-in policy rejects comma injection and unsafe native delimiters', () => {
  for (const rule of [
    'Read,Bash(*)',
    'Read\nBash(*)',
    'Bash(git status))',
    'Bash(git status;rm -rf /)'
  ]) {
    assert.throws(() => driver.buildCommand(environment({
      AGENTHUB_MODE: 'autonomous',
      AGENTHUB_ALLOWED_TOOLS: JSON.stringify([rule])
    }), true), /invalid Claude built-in policy entry/i);
  }
});

test('Claude built-in policy accepts only a non-broadening single-entry legacy value', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_ALLOWED_TOOLS: 'Read'
  }), true).args.slice(-2), ['--allowedTools', 'Read']);
  assert.throws(() => driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_ALLOWED_TOOLS: 'Read,Bash(*)'
  }), true), /invalid Claude built-in policy/i);
});

test('Claude shell policy fails closed for glob, compound, substitution, and redirection syntax', () => {
  for (const command of [
    'git *',
    'git status && rm -rf /',
    'echo $(whoami)',
    'echo safe,Write',
    'cat file > output'
  ]) {
    assert.throws(() => driver.buildCommand(environment({
      AGENTHUB_MODE: 'scheduled',
      AGENTHUB_ALLOWED_COMMANDS: JSON.stringify([command])
    }), true), /unsafe Claude shell policy entry/i);
  }
});

test('Claude MCP policy fails closed for partial or ambiguous native patterns', () => {
  for (const pattern of ['docs__search', 'mcp__docs__sea?ch', 'mcp__*__search']) {
    assert.throws(() => driver.buildCommand(environment({
      AGENTHUB_MODE: 'autonomous',
      AGENTHUB_ALLOWED_MCP_TOOLS: JSON.stringify([pattern])
    }), true), /invalid Claude MCP policy entry/i);
  }
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
  // Without pre-accepted trust the interactive TUI stops on its safety dialog and an
  // API-created session never starts the task it was given.
  assert.match(entrypoint, /claude\/workspace-trust\.mjs" "\$HOME\/\.claude\.json" "\$CLAUDE_WORKDIR"/);
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
  // Asserts the pinning mechanism, not the number — update-agent-runtimes.yml rewrites
  // the ARG, and a literal version here would fail on every bump.
  assert.match(dockerfile, /ARG CLAUDE_CODE_VERSION=\d+\.\d+\.\d+/);
  assert.match(dockerfile, /npm install -g @anthropic-ai\/claude-code@\$\{CLAUDE_CODE_VERSION\}/);
  assert.match(dockerfile, /claude --version 2>&1 \| grep -Fq "\$CLAUDE_CODE_VERSION"/);
  assert.match(dockerfile, /\/usr\/local\/bin\/claude/);
});
