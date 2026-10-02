'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const execPolicy = require('../../openclaw/exec-policy');

function workspace() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-openclaw-policy-'));
  return {
    configPath: path.join(directory, '.openclaw', 'openclaw.json'),
    approvalsPath: path.join(directory, '.openclaw', 'exec-approvals.json')
  };
}

function write(environment) {
  const paths = workspace();
  const result = execPolicy.writeExecPolicy(paths.configPath, paths.approvalsPath, environment);
  return {
    ...result,
    config: JSON.parse(fs.readFileSync(paths.configPath, 'utf8')),
    approvals: JSON.parse(fs.readFileSync(paths.approvalsPath, 'utf8')),
    paths
  };
}

// OpenClaw's documented baseline for an unconfigured host is `full` / `off` -- everything runs and
// nothing asks. That is what an unattended session used to get regardless of its auto-approve
// setting, because nothing in the runtime wrote this policy at all.
test('an unattended session that does not auto-approve is held to its allow list', () => {
  const written = write({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_AUTO_APPROVE: '0',
    AGENTHUB_ALLOWED_COMMANDS: JSON.stringify(['git status', '/usr/bin/uptime'])
  });

  assert.deepEqual(written.config.tools.exec, { security: 'allowlist', ask: 'off' });
  assert.deepEqual(written.approvals.defaults,
    { security: 'allowlist', ask: 'off', askFallback: 'deny' });
  assert.deepEqual(written.approvals.agents.main.allowlist,
    [{ pattern: '**/git' }, { pattern: '/usr/bin/uptime' }]);
});

test('an unattended session that auto-approves runs everything without asking', () => {
  const written = write({ AGENTHUB_MODE: 'autonomous', AGENTHUB_AUTO_APPROVE: '1' });

  assert.deepEqual(written.config.tools.exec, { security: 'full', ask: 'off' });
  assert.equal(written.approvals.defaults.security, 'full');
  assert.equal(written.approvals.defaults.ask, 'off');
});

test('an auto-approving session carries no allow list, which could only narrow it', () => {
  // The effective policy is the stricter of config and approvals, so an allow list next to
  // `security: full` would be a restriction nobody asked for.
  const written = write({
    AGENTHUB_MODE: 'scheduled',
    AGENTHUB_AUTO_APPROVE: '1',
    AGENTHUB_ALLOWED_COMMANDS: JSON.stringify(['git status'])
  });

  assert.deepEqual(written.approvals.agents, {});
});

test('an interactive session asks its own terminal, where a person is sitting', () => {
  const written = write({ AGENTHUB_MODE: 'interactive', AGENTHUB_AUTO_APPROVE: '1' });

  assert.deepEqual(written.config.tools.exec, { security: 'allowlist', ask: 'on-miss' });
  assert.equal(written.approvals.defaults.askFallback, 'deny');
});

test('a missing auto-approve variable is off, not on', () => {
  // A pod created before the variable existed must not be read as auto-approving.
  const written = write({ AGENTHUB_MODE: 'autonomous' });

  assert.equal(written.config.tools.exec.security, 'allowlist');
});

test('the agent id follows the session so the allow list reaches the right profile', () => {
  const written = write({
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_AUTO_APPROVE: '0',
    AGENTHUB_OPENCLAW_AGENT_ID: 'worker-2',
    AGENTHUB_ALLOWED_COMMANDS: JSON.stringify(['git status'])
  });

  assert.deepEqual(Object.keys(written.approvals.agents), ['worker-2']);
});

test('MCP servers written by the config step survive the policy step', () => {
  const paths = workspace();
  fs.mkdirSync(path.dirname(paths.configPath), { recursive: true });
  fs.writeFileSync(paths.configPath, JSON.stringify({
    mcp: { servers: { agenthub_files: { enabled: true, command: 'node' } } },
    tools: { exec: { security: 'full', ask: 'off' }, other: { keep: true } }
  }));

  execPolicy.writeExecPolicy(paths.configPath, paths.approvalsPath,
    { AGENTHUB_MODE: 'autonomous', AGENTHUB_AUTO_APPROVE: '0' });
  const config = JSON.parse(fs.readFileSync(paths.configPath, 'utf8'));

  assert.deepEqual(config.mcp.servers.agenthub_files, { enabled: true, command: 'node' });
  assert.deepEqual(config.tools.other, { keep: true });
  assert.equal(config.tools.exec.security, 'allowlist');
});

// ~/.openclaw is the state directory and comes back from the session's own archive, so merging
// would let an allow-list entry an earlier incarnation accumulated grant a permission the hub
// never did -- including one the agent added itself with `openclaw approvals allowlist add`.
test('a restored approvals file is replaced, never merged', () => {
  const paths = workspace();
  fs.mkdirSync(path.dirname(paths.approvalsPath), { recursive: true });
  fs.writeFileSync(paths.approvalsPath, JSON.stringify({
    version: 1,
    defaults: { security: 'full', ask: 'off' },
    agents: { main: { allowlist: [{ pattern: '**/curl' }, { pattern: '/bin/sh' }] } }
  }));

  execPolicy.writeExecPolicy(paths.configPath, paths.approvalsPath, {
    AGENTHUB_MODE: 'autonomous',
    AGENTHUB_AUTO_APPROVE: '0',
    AGENTHUB_ALLOWED_COMMANDS: JSON.stringify(['git status'])
  });
  const approvals = JSON.parse(fs.readFileSync(paths.approvalsPath, 'utf8'));

  assert.deepEqual(approvals.agents.main.allowlist, [{ pattern: '**/git' }]);
  assert.equal(approvals.defaults.security, 'allowlist');
});

test('only the binary survives the translation', () => {
  // AgentHub stores command prefixes, OpenClaw matches a glob against the resolved binary. The
  // argument part has nowhere to go, so "git status" becomes a licence to run git with any
  // arguments at all -- a widening, and the reason this is only reached in allowlist mode where
  // the alternative is an agent that cannot run anything.
  assert.deepEqual(execPolicy.allowlistPatterns(['git status --short', 'dotnet test']),
    ['**/git', '**/dotnet']);
  assert.deepEqual(execPolicy.allowlistPatterns(['git status', 'git push']), ['**/git']);
  // Written down because it is the uncomfortable case: a policy that allowed one careful rm
  // grants every rm. Narrowing it would need argPattern, which AgentHub does not store.
  assert.deepEqual(execPolicy.allowlistPatterns(['rm -rf /tmp/build']), ['**/rm']);
  // A path keeps its anchor instead of being widened to any binary of that name.
  assert.deepEqual(execPolicy.allowlistPatterns(['/usr/bin/uptime']), ['/usr/bin/uptime']);
  // Glob syntax in the binary itself is not something a command prefix could have meant.
  assert.deepEqual(execPolicy.allowlistPatterns(['*sh -c echo', 'ls ', '  ']), ['**/ls']);
});

test('a malformed shell policy is fatal rather than silently empty', () => {
  const paths = workspace();

  assert.throws(() => execPolicy.writeExecPolicy(paths.configPath, paths.approvalsPath, {
    AGENTHUB_MODE: 'autonomous', AGENTHUB_ALLOWED_COMMANDS: '{not json'
  }), /Invalid OpenClaw shell policy JSON/);
  assert.throws(() => execPolicy.writeExecPolicy(paths.configPath, paths.approvalsPath, {
    AGENTHUB_MODE: 'autonomous', AGENTHUB_ALLOWED_COMMANDS: '[1,2]'
  }), /Invalid OpenClaw shell policy JSON/);
});

test('both files are written with owner-only permissions', () => {
  const written = write({ AGENTHUB_MODE: 'autonomous', AGENTHUB_AUTO_APPROVE: '1' });

  if (process.platform === 'win32') return;
  for (const file of [written.paths.configPath, written.paths.approvalsPath]) {
    assert.equal(fs.statSync(file).mode & 0o777, 0o600, file);
  }
});

test('the entrypoint writes the policy after the MCP config and reports it', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'openclaw', 'entrypoint.sh'), 'utf8');
  const mcpStep = entrypoint.indexOf('openclaw/mcp-config.js');
  const policyStep = entrypoint.indexOf('openclaw/exec-policy.js');

  assert.ok(mcpStep > 0 && policyStep > 0);
  // Both write openclaw.json; the policy step merges into what the MCP step produced.
  assert.ok(policyStep > mcpStep);
  assert.ok(entrypoint.includes('exec-approvals.json'));
  assert.ok(entrypoint.includes('OpenClaw exec policy:'));
});

test('the exec policy module ships in the OpenClaw image', () => {
  const dockerfile = fs.readFileSync(path.join(runtimeDir, 'openclaw', 'Dockerfile'), 'utf8');

  assert.ok(dockerfile.includes('COPY openclaw /opt/session-agent/openclaw'));
});
