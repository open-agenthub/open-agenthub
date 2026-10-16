'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const driver = require('../../openclaw/driver');

function environment(overrides = {}) {
  return {
    AGENTHUB_MODE: 'interactive', AGENTHUB_PROMPT: '', AGENTHUB_RESUME: '0',
    AGENTHUB_STATE_RESTORED: '0', ...overrides
  };
}

test('OpenClaw driver exposes the provider state contract', () => {
  assert.equal(driver.name, 'OpenClaw');
  assert.equal(driver.stateDir, '.openclaw');
  // Pinned from OpenClaw 2026.7.1-2: auth-profiles.json (logical JSON / SQLite store_json).
  assert.equal(driver.authFilename, 'auth-profiles.json');
  assert.deepEqual(driver.attachmentCapabilities, {
    nativeImages: false,
    localImagePaths: true,
    mcpImages: true
  });
  assert.deepEqual(driver.stateExcludes, [
    '.openclaw/agents/main/agent/auth-profiles.json',
    '.openclaw/agents/main/agent/openclaw-agent.sqlite',
    '.openclaw/agents/main/agent/openclaw-agent.sqlite-wal',
    '.openclaw/agents/main/agent/openclaw-agent.sqlite-shm',
    '.openclaw/agents/*/agent/auth-profiles.json',
    '.openclaw/agents/*/agent/openclaw-agent.sqlite',
    '.openclaw/agents/*/agent/openclaw-agent.sqlite-wal',
    '.openclaw/agents/*/agent/openclaw-agent.sqlite-shm'
  ]);
  assert.equal(typeof driver.prepare, 'function');
});

test('OpenClaw interactive starts local TUI without resume flags', () => {
  assert.deepEqual(driver.buildCommand(environment(), true), {
    cmd: 'openclaw', args: ['tui', '--local']
  });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1'
  }), false), { cmd: 'openclaw', args: ['tui', '--local'] });
});

test('OpenClaw interactive session starts on its prompt, except when resuming', () => {
  // `tui --message` submits the task once the TUI is up and keeps it live, so an API-created
  // session is already working when a person opens it. `agent --message` would answer and exit,
  // leaving nothing to take over.
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build'
  }), true), {
    cmd: 'openclaw', args: ['tui', '--local', '--message', 'triage the failing build']
  });

  // The restored session already holds the task; sending it again would restart the work.
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build',
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'sess-1'
  }), true), { cmd: 'openclaw', args: ['tui', '--local', '--session', 'sess-1'] });

  // A resume that was asked for but has no restored state is a fresh start, so the prompt applies.
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_PROMPT: 'triage the failing build',
    AGENTHUB_RESUME: '1', AGENTHUB_STATE_RESTORED: '0',
    AGENTHUB_CLAUDE_SESSION_ID: 'sess-1'
  }), true).args, ['tui', '--local', '--message', 'triage the failing build']);

  const loginSh = path.join(runtimeDir, 'openclaw', 'login.sh');
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_OPENCLAW_LOGIN: '1', AGENTHUB_PROMPT: 'triage the failing build'
  }), true), {
    cmd: 'bash', args: [loginSh, 'tui', '--local', '--message', 'triage the failing build']
  });
  // --message must not be mistaken for a resume: that would skip the fresh-start path entirely.
  assert.equal(driver.isResumeCommand({
    cmd: 'openclaw', args: ['tui', '--local', '--message', 'triage the failing build']
  }), false);
});

test('OpenClaw subscription login runs inside the agent PTY before the interactive TUI', () => {
  const loginSh = path.join(runtimeDir, 'openclaw', 'login.sh');
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_OPENCLAW_LOGIN: '1'
  }), true), { cmd: 'bash', args: [loginSh, 'tui', '--local'] });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_OPENCLAW_LOGIN: '1',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'sess-1'
  }), true), {
    cmd: 'bash',
    args: [loginSh, 'tui', '--local', '--session', 'sess-1']
  });
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: [loginSh, 'tui', '--local', '--session', 'sess-1']
  }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: [loginSh, 'tui', '--local']
  }), false);

  const script = fs.readFileSync(loginSh, 'utf8');
  // Ordering is the point, not the exact body: the login prompt comes first, the export of what
  // it wrote second, and the model that matches the provider it just chose third — all inside
  // the "no auth file yet" branch, and all before the TUI is exec'd below.
  assert.match(script, new RegExp(
    'if \\[ ! -f "\\$\\{OPENCLAW_AUTH_FILE:-\\}" \\]; then' +
    '[\\s\\S]*?openclaw models auth add' +
    '[\\s\\S]*?sync-auth-profiles\\.js" export' +
    '[\\s\\S]*?apply_default_model' +
    '[\\s\\S]*?\\nfi'));
  assert.match(script, /sync-auth-profiles\.js" export/);
  assert.match(script, /^exec openclaw "\$@"$/m);
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'openclaw', 'entrypoint.sh'), 'utf8');
  assert.match(entrypoint, /export AGENTHUB_OPENCLAW_LOGIN=1/);
  assert.doesNotMatch(entrypoint, /^\s*openclaw models auth add\s*$/m);
});

test('OpenClaw interactive resume uses --session with session id when present', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'sess-1'
  }), true), {
    cmd: 'openclaw',
    args: ['tui', '--local', '--session', 'sess-1']
  });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_OPENCLAW_SESSION_ID: 'oc-9'
  }), true), {
    cmd: 'openclaw',
    args: ['tui', '--local', '--session', 'oc-9']
  });
});

test('OpenClaw autonomous and scheduled use local agent with message', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous', AGENTHUB_PROMPT: 'fix it'
  }), true), {
    cmd: 'openclaw',
    args: ['agent', '--local', '--agent', 'main', '--message', 'fix it']
  });
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'scheduled',
    AGENTHUB_PROMPT: 'report',
    AGENTHUB_OPENCLAW_AGENT_ID: 'ops'
  }), true), {
    cmd: 'openclaw',
    args: ['agent', '--local', '--agent', 'ops', '--message', 'report']
  });
});

test('OpenClaw autonomous resume keeps --session-id before --message', () => {
  assert.deepEqual(driver.buildCommand(environment({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_PROMPT: 'continue',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    AGENTHUB_CLAUDE_SESSION_ID: 'sess-9'
  }), true), {
    cmd: 'openclaw',
    args: [
      'agent', '--local', '--agent', 'main',
      '--session-id', 'sess-9', '--message', 'continue'
    ]
  });
});

test('OpenClaw resume recognition rejects fresh and merely resume-like commands', () => {
  const loginSh = path.join(runtimeDir, 'openclaw', 'login.sh');
  assert.equal(driver.isResumeCommand({
    cmd: 'openclaw', args: ['tui', '--local', '--session', 'sess-1']
  }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'openclaw',
    args: ['agent', '--local', '--agent', 'main', '--session-id', 'sess-1', '--message', 'p']
  }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: [loginSh, 'tui', '--local', '--session', 'sess-1']
  }), true);
  assert.equal(driver.isResumeCommand({
    cmd: 'bash', args: [loginSh, 'tui', '--local']
  }), false);
  assert.equal(driver.isResumeCommand({
    cmd: 'openclaw', args: ['tui', '--local']
  }), false);
  assert.equal(driver.isResumeCommand({
    cmd: 'openclaw', args: ['agent', '--local', '--agent', 'main', '--session-id']
  }), false);
  assert.equal(driver.isResumeCommand({
    cmd: 'other', args: ['--session-id', 'x']
  }), false);
});

test('OpenClaw missing-resume fallback requires representative missing-session output', () => {
  assert.equal(driver.isMissingResume('No session found with id sess-1', 1), true);
  assert.equal(driver.isMissingResume('Session not found', 1), true);
  assert.equal(driver.isMissingResume('Unknown session id sess-1', 1), true);
  assert.equal(driver.isMissingResume('network failure', 1), false);
  assert.equal(driver.isMissingResume('No session found with id sess-1', 0), false);
});

test('OpenClaw prepare scrubs provider API keys and passes only present ones to the child', () => {
  const anthropic = environment({
    AGENTHUB_MODE: 'autonomous',
    ANTHROPIC_API_KEY: 'synthetic-anthropic',
    OPENAI_API_KEY: 'synthetic-openai',
    CURSOR_API_KEY: 'synthetic-cursor'
  });
  assert.deepEqual(driver.prepare(anthropic), {
    childEnv: {
      ANTHROPIC_API_KEY: 'synthetic-anthropic',
      OPENAI_API_KEY: 'synthetic-openai',
      CURSOR_API_KEY: 'synthetic-cursor'
    }
  });
  assert.equal(anthropic.ANTHROPIC_API_KEY, undefined);
  assert.equal(anthropic.OPENAI_API_KEY, undefined);
  assert.equal(anthropic.CURSOR_API_KEY, undefined);

  const onlyCursor = environment({ CURSOR_API_KEY: 'synthetic-cursor-only' });
  assert.deepEqual(driver.prepare(onlyCursor), {
    childEnv: { CURSOR_API_KEY: 'synthetic-cursor-only' }
  });
  assert.equal(onlyCursor.CURSOR_API_KEY, undefined);

  const none = environment();
  assert.equal(driver.prepare(none), undefined);
});

test('OpenClaw entrypoint owns state dir, auth mode, watcher, and stale-auth ordering', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'openclaw', 'entrypoint.sh'), 'utf8');
  assert.match(entrypoint, /export AGENTHUB_STATE_DIR=\.openclaw/);
  assert.match(entrypoint, /source "\$COMMON_ENTRYPOINT"/);
  assert.match(entrypoint, /OPENCLAW_STATE_DIR=|HOME\/\.openclaw/);
  assert.match(entrypoint, /OPENCLAW_AGENT_DIR=.*agents\/.*\/agent/);
  assert.match(entrypoint, /OPENCLAW_AUTH_FILE=.*auth-profiles\.json/);
  assert.match(entrypoint, /rm -f "\$OPENCLAW_AUTH_FILE"/);
  assert.match(entrypoint, /agents\/\*\/agent\/auth-profiles\.json/);
  assert.match(entrypoint, /agents\/\*\/agent\/openclaw-agent\.sqlite/);
  assert.match(entrypoint, /\/secrets\/openclaw\/auth-profiles\.json/);
  assert.match(entrypoint, /sync-auth-profiles\.js" import/);
  assert.match(entrypoint, /sync-auth-profiles\.js" export/);
  assert.match(entrypoint, /auth-watcher\.js/);
  assert.match(entrypoint, /AGENTHUB_OPENCLAW_AUTH_EXPECT_CREATE/);
  assert.match(entrypoint, /AGENTHUB_OPENCLAW_AUTH_BASELINE_SHA256/);
  assert.match(entrypoint, /AGENTHUB_OPENCLAW_LOGIN/);
  assert.match(entrypoint, /ANTHROPIC_API_KEY|OPENAI_API_KEY|CURSOR_API_KEY/);
  assert.match(entrypoint, /subscription\)/);
  assert.match(entrypoint, /apikey\)/);
  assert.match(entrypoint, /AGENTHUB_DRIVER="\$RUNTIME\/openclaw\/driver\.js"/);
  assert.ok(entrypoint.indexOf('source "$COMMON_ENTRYPOINT"') <
    entrypoint.indexOf('rm -f "$OPENCLAW_AUTH_FILE"'));
  assert.ok(entrypoint.indexOf('rm -f "$OPENCLAW_AUTH_FILE"') <
    entrypoint.indexOf('/secrets/openclaw/auth-profiles.json'));
  assert.ok(entrypoint.indexOf('sync-auth-profiles.js" import') <
    entrypoint.indexOf('sync-auth-profiles.js" export'));
  assert.ok(entrypoint.indexOf('sync-auth-profiles.js" export') <
    entrypoint.indexOf('AUTH_BASELINE_SHA256="$(node -e'));
});

test('OpenClaw image installs CLI and preserves custom-image injection paths', () => {
  const dockerfile = fs.readFileSync(path.join(runtimeDir, 'openclaw', 'Dockerfile'), 'utf8');
  assert.match(dockerfile, /COPY common\s+\/opt\/session-agent\/common/);
  assert.match(dockerfile, /COPY openclaw\s+\/opt\/session-agent\/openclaw/);
  assert.match(dockerfile, /COPY openclaw\/entrypoint\.sh\s+\/usr\/local\/bin\/entrypoint\.sh/);
  assert.match(dockerfile, /ARG OPENCLAW_VERSION=2026\.7\.1-2/);
  assert.match(dockerfile, /npm install -g openclaw@\$\{OPENCLAW_VERSION\}/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/node/);
  assert.match(dockerfile, /test -x \/usr\/local\/bin\/openclaw/);
  assert.doesNotMatch(dockerfile, /@anthropic-ai|@openai\/codex|COPY claude|COPY codex|COPY cursor/);
});

// OpenClaw reads credentials from SQLite, so a swapped file must be imported there too — the
// step the entrypoint and login.sh perform — or the restarted agent keeps the previous login.
test('OpenClaw installCredential writes the hub file and imports it into the agent SQLite store', () => {
  const os = require('node:os');
  const { readStoreFromSqlite } = require('../../openclaw/sync-auth-profiles');
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'openclaw-install-'));
  const stateDir = path.join(root, '.openclaw');
  const agentDir = path.join(stateDir, 'agents', 'main', 'agent');
  const env = {
    HOME: root, OPENCLAW_STATE_DIR: stateDir, OPENCLAW_AGENT_DIR: agentDir,
    OPENCLAW_AUTH_FILE: path.join(stateDir, 'auth-profiles.json'), AGENTHUB_OPENCLAW_AGENT_ID: 'main'
  };
  const body = Buffer.from(JSON.stringify({
    version: 1, profiles: { 'anthropic:work': { type: 'api_key', provider: 'anthropic', key: 'synthetic-key-swapped' } }
  }));

  assert.equal(driver.credentialPath(env), env.OPENCLAW_AUTH_FILE);
  assert.equal(driver.validCredential(body), true);
  assert.equal(driver.validCredential(Buffer.from('{"profiles":{}}')), false);
  driver.installCredential(env, body, env.OPENCLAW_AUTH_FILE);

  assert.equal(fs.readFileSync(env.OPENCLAW_AUTH_FILE, 'utf8'), body.toString());
  const store = readStoreFromSqlite(path.join(agentDir, 'openclaw-agent.sqlite'));
  assert.equal(store.profiles['anthropic:work'].key, 'synthetic-key-swapped');
});
