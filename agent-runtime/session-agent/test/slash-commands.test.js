'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs/promises');
const realFs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { listSlashCommands, frontmatter, MAX_COMMANDS } = require('../../common/slash-commands');
const { validateDriver } = require('../../common/driver-contract');
const { createHarness, requestHttp } = require('./common-server-harness');

const AGENTS = ['claude', 'codex', 'cursor', 'openclaw', 'opencode'];

async function tempRoot(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'slash-commands-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  return root;
}

async function write(file, text) {
  await fs.mkdir(path.dirname(file), { recursive: true });
  await fs.writeFile(file, text);
}

test('every runtime declares a valid catalog of unique, typeable built-ins', () => {
  for (const agent of AGENTS) {
    const driver = require(`../../${agent}/driver.js`);
    validateDriver(driver);
    const { builtins, cli } = driver.slashCommands;
    assert.ok(cli, agent);
    assert.ok(builtins.length > 10, agent);
    const names = builtins.map(entry => entry.name);
    assert.equal(new Set(names).size, names.length, `${agent} lists a command twice`);
    for (const entry of builtins) {
      assert.match(entry.name, /^[a-z][a-z0-9-]*$/, `${agent}/${entry.name}`);
      assert.ok(entry.description, `${agent}/${entry.name} has no description`);
      assert.ok(entry.effect === undefined || entry.effect === 'new-conversation', `${agent}/${entry.name}`);
    }
  }
});

test('the conversation-switching commands are marked, so the composer can warn before sending them', () => {
  const effects = agent => require(`../../${agent}/driver.js`).slashCommands.builtins
    .filter(entry => entry.effect === 'new-conversation').map(entry => entry.name);
  assert.ok(effects('claude').includes('clear'));
  assert.ok(effects('codex').includes('new'));
  assert.ok(effects('opencode').includes('new'));
  assert.ok(effects('openclaw').includes('reset'));
  assert.ok(effects('cursor').includes('clear'));
  assert.equal(effects('claude').includes('compact'), false);
});

test('frontmatter reads flat keys and unquotes values', () => {
  assert.deepEqual(frontmatter('---\nname: x\ndescription: "Say: hi"\nargument-hint: <env>\n---\nbody'),
    { name: 'x', description: 'Say: hi', 'argument-hint': '<env>' });
  assert.deepEqual(frontmatter('no frontmatter'), {});
});

test('Claude: custom commands (with namespaces) and skills from the user and the project', async t => {
  const root = await tempRoot(t);
  const home = path.join(root, 'home'); const cwd = path.join(root, 'work');
  await write(path.join(home, '.claude/commands/deploy-check.md'), '---\ndescription: Check the deploy\nargument-hint: <env>\n---\nCheck $ARGUMENTS');
  await write(path.join(cwd, '.claude/commands/ops/restart.md'), '---\ndescription: Restart\n---\n');
  await write(path.join(home, '.claude/skills/rig-skill/SKILL.md'), '---\nname: rig-skill\ndescription: A rig skill\n---\n');
  await write(path.join(home, '.claude/skills/hidden/SKILL.md'), '---\nname: hidden\nuser-invocable: false\n---\n');
  await write(path.join(home, '.claude/skills/no-skill-file/README.md'), 'not a skill');
  // A name a CLI could not take after its "/" — and one that would type a newline into it.
  await write(path.join(home, '.claude/commands/has space.md'), '');
  await write(path.join(home, '.claude/skills/evil/SKILL.md'), '---\nname: "evil\\nrm -rf"\n---\n');
  // A custom command that reuses a built-in's name is shadowed by the built-in.
  await write(path.join(home, '.claude/commands/model.md'), '---\ndescription: Not the real one\n---\n');
  const catalog = require('../../claude/slash-commands');
  const result = listSlashCommands(catalog, { env: {}, home, cwd, fs: realFs });
  const byName = Object.fromEntries(result.commands.map(entry => [entry.name, entry]));
  assert.deepEqual(byName['deploy-check'], { name: 'deploy-check', source: 'custom', description: 'Check the deploy', argumentHint: '<env>' });
  assert.equal(byName['ops:restart'].source, 'custom');
  assert.deepEqual(byName['rig-skill'], { name: 'rig-skill', source: 'skill', description: 'A rig skill' });
  assert.equal(byName.hidden, undefined);
  assert.equal(byName['no-skill-file'], undefined);
  assert.equal(byName['has space'], undefined);
  assert.equal(result.commands.some(entry => entry.name.includes('evil')), false);
  assert.equal(byName.model.source, 'builtin');
  assert.equal(byName.clear.effect, 'new-conversation');
  assert.equal(result.truncated, false);
});

test('OpenCode: commands from its JSON config and command directories', async t => {
  const root = await tempRoot(t);
  const home = path.join(root, 'home'); const cwd = path.join(root, 'work');
  const config = path.join(home, '.config/opencode/opencode.json');
  await write(config, JSON.stringify({ command: { 'deploy-check': { template: 'x', description: 'Check the deploy' } } }));
  await write(path.join(cwd, '.opencode/command/lint.md'), '---\ndescription: Lint it\n---\n');
  await write(path.join(cwd, 'opencode.json'), '{ not json');
  const result = listSlashCommands(require('../../opencode/slash-commands'),
    { env: { AGENTHUB_OPENCODE_CONFIG: config }, home, cwd, fs: realFs });
  const names = result.commands.filter(entry => entry.source === 'custom').map(entry => entry.name);
  assert.deepEqual(names, ['deploy-check', 'lint']);
});

test('symlinked command files are skipped and the list is bounded', async t => {
  const root = await tempRoot(t);
  const dir = path.join(root, 'commands');
  await write(path.join(root, 'outside.md'), '---\ndescription: outside\n---\n');
  await fs.mkdir(dir);
  await fs.symlink(path.join(root, 'outside.md'), path.join(dir, 'linked.md'));
  for (let i = 0; i < MAX_COMMANDS + 5; i++) await fs.writeFile(path.join(dir, `c${i}.md`), '');
  const catalog = { cli: 'Test', builtins: [], sources: () => [{ kind: 'commands', dir }] };
  const result = listSlashCommands(catalog, { env: {}, home: root, cwd: root, fs: realFs });
  assert.equal(result.commands.some(entry => entry.name === 'linked'), false);
  assert.equal(result.commands.length, MAX_COMMANDS);
  assert.equal(result.truncated, true);
});

test('HTTP route authenticates and answers GET only', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' },
    { slashCommands: { cli: 'Fixture 1', builtins: [{ name: 'model', description: 'Pick a model' }] } });
  assert.equal((await requestHttp(harness, 'GET', '/agenthub/commands', {})).status, 401);
  const auth = { 'X-Agent-Token': 'correct-token' };
  assert.equal((await requestHttp(harness, 'POST', '/agenthub/commands', auth)).status, 405);
  const response = await requestHttp(harness, 'GET', '/agenthub/commands', auth);
  assert.equal(response.status, 200);
  assert.equal(response.headers['cache-control'], 'no-store');
  const body = JSON.parse(response.body);
  assert.equal(body.cli, 'Fixture 1');
  assert.deepEqual(body.commands, [{ name: 'model', source: 'builtin', description: 'Pick a model' }]);
});

test('a runtime without a catalog answers an empty list rather than an error', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  const response = await requestHttp(harness, 'GET', '/agenthub/commands', { 'X-Agent-Token': 'correct-token' });
  assert.equal(response.status, 200);
  assert.deepEqual(JSON.parse(response.body).commands, []);
});

test('real common server with the real Claude driver serves its commands over TCP HTTP', { timeout: 15000 }, async t => {
  const { once } = require('node:events');
  const { createCommonServer } = require('../../common/server');
  const root = await tempRoot(t);
  await write(path.join(root, '.claude/skills/rig-skill/SKILL.md'), '---\nname: rig-skill\ndescription: A rig skill\n---\n');
  let child;
  const claude = require('../../claude/driver.js');
  const runtime = createCommonServer({
    env: { PATH: process.env.PATH, HOME: root, AGENTHUB_WORKDIR: root, AGENTHUB_WORKSPACE_ROOT: root,
      AGENTHUB_PORT: '0', AGENTHUB_SESSION_ID: 'fixture', AGENTHUB_MODE: 'interactive',
      AGENTHUB_RESUME: '0', AGENTHUB_PROMPT: '', AGENTHUB_SKILLS_SYNC_SECONDS: '0',
      AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:1', AGENTHUB_CALLBACK_TOKEN: 'fixture-token', AGENTHUB_FILE_ROOT: path.join(root, 'files') },
    // The real catalog on a stand-in process: the route must not depend on the CLI running.
    driver: { ...claude, prepare() {}, buildCommand: () => ({ cmd: process.execPath, args: ['-e', 'process.stdin.resume()'], pipe: true }),
      isResumeCommand: () => false, isMissingResume: () => false, findTranscript: undefined },
    dependencies: { pty: {}, WebSocketServer: require('ws').WebSocketServer, spawn: (cmd, args, options) => { child = require('node:child_process').spawn(cmd, args, options); return child; },
      process: { on() {}, exit() {} }, setInterval: () => ({ unref() {} }) }
  });
  t.after(async () => {
    runtime.webSocketServer.close(); runtime.httpServer.closeAllConnections();
    await new Promise(resolve => runtime.httpServer.close(resolve));
    if (child && child.exitCode === null) { child.removeAllListeners('exit'); const stopped = once(child, 'exit'); child.kill(); await stopped; }
  });
  if (!runtime.httpServer.listening) await once(runtime.httpServer, 'listening');
  const url = `http://127.0.0.1:${runtime.httpServer.address().port}/agenthub/commands`;
  assert.equal((await fetch(url)).status, 401);
  const response = await fetch(url, { headers: { 'X-Agent-Token': 'fixture-token' } });
  assert.equal(response.status, 200);
  const body = await response.json();
  assert.equal(body.agent, 'Claude');
  assert.ok(body.commands.some(entry => entry.name === 'compact' && entry.source === 'builtin'));
  assert.ok(body.commands.some(entry => entry.name === 'rig-skill' && entry.source === 'skill'));
});
