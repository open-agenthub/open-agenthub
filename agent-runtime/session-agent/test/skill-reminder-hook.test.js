const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const test = require('node:test');

const runtimeRoot = path.join(__dirname, '..', '..');
const hookPath = path.join(runtimeRoot, 'common', 'skill-reminder-hook.mjs');
// Same resolution as mcp-policy-hook.test.js: on Windows a bare "bash" can resolve to
// WSL's, which sees none of the paths this test passes it.
const bashPath = process.platform === 'win32' ? 'C:\\Program Files\\Git\\bin\\bash.exe' : 'bash';

function markerHome() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'skill-reminder-'));
  test.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return { TMPDIR: dir };
}

const claudeStop = { hook_event_name: 'Stop', session_id: 's1', stop_hook_active: false };
const cursorStop = { conversation_id: 'c1', generation_id: 'g1', status: 'completed', loop_count: 0 };

test('a turn that only answered a question is left alone', async () => {
  const { decide } = await import('../../common/skill-reminder-hook.mjs');
  assert.equal(decide(claudeStop, markerHome()), null);
});

test('a turn that changed something is reminded, in the agent\'s own dialect', async () => {
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  mark({ ...claudeStop, tool_name: 'Edit' }, 'work', env);
  const claude = decide(claudeStop, env);
  assert.equal(claude.decision, 'block');
  assert.match(claude.reason, /upload_skill/);
  assert.match(claude.reason, /reusable procedure/);

  mark({ ...cursorStop, tool_name: undefined }, 'work', env);
  const cursor = decide(cursorStop, env);
  assert.match(cursor.followup_message, /upload_skill/);
  assert.equal(cursor.decision, undefined);
});

test('the reminder fires once: the marker is spent by the turn it belongs to', async () => {
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  mark({ ...claudeStop, tool_name: 'Bash' }, 'work', env);
  assert.ok(decide(claudeStop, env));
  // The next turn has to earn its own reminder, or every later answer inherits this one.
  assert.equal(decide(claudeStop, env), null);
});

test('a turn that already uploaded is not asked to upload', async () => {
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  mark({ ...claudeStop, tool_name: 'Write' }, 'work', env);
  mark({ ...claudeStop, tool_name: 'mcp__skill-library__upload_skill' }, 'uploaded', env);

  assert.equal(decide(claudeStop, env), null);
});

test('an upload is recognised even when the hook only says --mark work', async () => {
  // Codex cannot express a per-tool matcher in its managed hooks, so every tool arrives
  // as "work" and the tool name is the only thing that distinguishes them.
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  mark({ ...claudeStop, tool_name: 'shell' }, 'work', env);
  mark({ ...claudeStop, tool_name: 'mcp__skill-library__upload_skill' }, 'work', env);

  assert.equal(decide(claudeStop, env), null);
});

test('read-only tools do not count as work', async () => {
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  for (const tool of ['Read', 'Grep', 'Glob', 'WebFetch', 'update_plan', 'view_image']) {
    assert.equal(mark({ ...claudeStop, tool_name: tool }, 'work', env), null, tool);
  }
  assert.equal(decide(claudeStop, env), null);
});

test('the continuation a reminder caused does not remind again', async () => {
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  mark({ ...claudeStop, tool_name: 'Edit' }, 'work', env);
  assert.equal(decide({ ...claudeStop, stop_hook_active: true }, env), null);
  mark({ ...cursorStop }, 'work', env);
  assert.equal(decide({ ...cursorStop, loop_count: 1 }, env), null);
});

test('an aborted turn is not a finished piece of work', async () => {
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  mark({ ...cursorStop }, 'work', env);
  assert.equal(decide({ ...cursorStop, status: 'aborted' }, env), null);
});

test('separate sessions do not consume each other\'s markers', async () => {
  const { decide, mark } = await import('../../common/skill-reminder-hook.mjs');
  const env = markerHome();

  mark({ session_id: 'one', tool_name: 'Edit' }, 'work', env);
  assert.equal(decide({ ...claudeStop, session_id: 'two' }, env), null);
  assert.ok(decide({ ...claudeStop, session_id: 'one' }, env));
});

test('run as a command it reads the payload from stdin and prints the decision', () => {
  const env = { ...process.env, ...markerHome() };
  const payload = JSON.stringify({ ...claudeStop, tool_name: 'Edit' });

  execFileSync(process.execPath, [hookPath, '--mark', 'work'], { input: payload, env });
  const out = execFileSync(process.execPath, [hookPath], { input: payload, env, encoding: 'utf8' });

  assert.match(JSON.parse(out).reason, /skill library/);
  // And silence the second time, printing nothing at all rather than an empty object.
  assert.equal(execFileSync(process.execPath, [hookPath], { input: payload, env, encoding: 'utf8' }), '');
});

test('a broken payload never breaks the turn', () => {
  const env = { ...process.env, ...markerHome() };
  for (const input of ['', 'not json', '[]']) {
    assert.equal(execFileSync(process.execPath, [hookPath], { input, env, encoding: 'utf8' }), '');
  }
});

test('the codex wrapper runs the shared hook from its managed directory', () => {
  const env = { ...process.env, ...markerHome() };
  const wrapper = path.join(runtimeRoot, 'codex', 'skill-reminder-hook.js');
  const payload = JSON.stringify({ session_id: 'codex-1', tool_name: 'apply_patch' });

  execFileSync(process.execPath, [wrapper, '--mark', 'work'], { input: payload, env });
  const out = execFileSync(process.execPath, [wrapper], {
    input: JSON.stringify({ session_id: 'codex-1', hook_event_name: 'Stop' }), env, encoding: 'utf8'
  });

  assert.equal(JSON.parse(out).decision, 'block');
});

test('every runtime wires the reminder to its own end-of-turn hook', () => {
  const settings = execFileSync(
    bashPath, [path.join(runtimeRoot, 'claude', 'hooks', 'mcp-policy-hook.sh'), '--settings'],
    { encoding: 'utf8', env: { ...process.env, AGENTHUB_MODE: 'interactive', RUNTIME: '/opt/session-agent' } });
  const parsed = JSON.parse(settings);
  assert.ok(parsed.hooks.Stop[0].hooks[0].command.includes('skill-reminder-hook.mjs'));
  assert.ok(parsed.hooks.PostToolUse.some(entry => /Edit\|Write/.test(entry.matcher)));

  // Autonomous sessions get it too — nobody is there to ask the agent afterwards.
  const autonomous = JSON.parse(execFileSync(
    bashPath, [path.join(runtimeRoot, 'claude', 'hooks', 'mcp-policy-hook.sh'), '--settings'],
    { encoding: 'utf8', env: { ...process.env, AGENTHUB_MODE: 'autonomous', RUNTIME: '/opt/session-agent' } }));
  assert.ok(autonomous.hooks.Stop[0].hooks[0].command.includes('skill-reminder-hook.mjs'));

  const requirements = fs.readFileSync(path.join(runtimeRoot, 'codex', 'requirements.toml'), 'utf8');
  assert.match(requirements, /\[\[hooks\.Stop\]\]/);
  // Managed hooks must live under managed_dir, and project-requirements.js rewrites exactly
  // that path — a command pointing straight at common/ would survive neither rule.
  assert.match(requirements, /command = "node \/opt\/session-agent\/codex\/skill-reminder-hook\.js"/);

  const { hooksConfig } = require('../../cursor/hooks-config.js');
  const cursor = JSON.parse(hooksConfig('/opt/session-agent'));
  assert.equal(cursor.hooks.stop[0].loop_limit, 1);
  assert.match(cursor.hooks.afterFileEdit[0].command, /--mark work$/);
  assert.match(cursor.hooks.beforeShellExecution[0].command, /--mark work$/);

  const cursorEntrypoint = fs.readFileSync(path.join(runtimeRoot, 'cursor', 'entrypoint.sh'), 'utf8');
  assert.match(cursorEntrypoint, /hooks-config\.js" > "\$CURSOR_CONFIG_DIR\/hooks\.json"/);

  for (const provider of ['claude', 'codex', 'cursor', 'openclaw']) {
    const dockerfile = fs.readFileSync(path.join(runtimeRoot, provider, 'Dockerfile'), 'utf8');
    assert.match(dockerfile, /COPY common \/opt\/session-agent\/common/);
  }
});
