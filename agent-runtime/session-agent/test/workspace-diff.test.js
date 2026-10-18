'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createWorkspaceDiff } = require('../../common/workspace-diff');
const { createHarness, requestHttp } = require('./common-server-harness');
const git = (cwd, ...args) => execFileSync('git', args, { cwd, encoding: 'utf8' });
async function repository(root, name) {
  const dir = path.join(root, name); await fs.mkdir(dir, { recursive: true });
  git(dir, 'init', '-b', 'main'); git(dir, 'config', 'user.name', 'Test'); git(dir, 'config', 'user.email', 'test@example.com');
  await fs.writeFile(path.join(dir, 'code.txt'), 'before\n');
  git(dir, 'add', '.'); git(dir, 'commit', '-qm', 'Initial');
  return dir;
}

test('real Git covers repositories, staged/unstaged/untracked, branch commits, binary, deletion and bounds', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'workspace-diff-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const first = await repository(root, 'first');
  const second = await repository(root, 'second');
  git(first, 'checkout', '-qb', 'feature');
  await fs.writeFile(path.join(first, 'committed.txt'), 'branch commit\n');
  git(first, 'add', '.'); git(first, 'commit', '-qm', 'Feature');
  await fs.writeFile(path.join(first, 'code.txt'), 'staged\n'); git(first, 'add', '.');
  await fs.appendFile(path.join(first, 'code.txt'), 'unstaged\n');
  await fs.writeFile(path.join(first, 'new file.txt'), '<script>untrusted</script>\n');
  await fs.writeFile(path.join(first, 'binary.dat'), Buffer.from([0, 1, 2]));
  await fs.writeFile(path.join(first, 'large.txt'), 'x'.repeat(70000));
  await fs.writeFile(path.join(first, '.gitignore'), 'ignored.txt\n');
  await fs.writeFile(path.join(first, 'ignored.txt'), 'private\n');
  await fs.symlink('/etc/passwd', path.join(first, 'link'));
  await fs.unlink(path.join(second, 'code.txt'));
  const read = createWorkspaceDiff(root);
  const base = await read('base');
  assert.equal(base.repositories.length, 2);
  const files = base.repositories.find(r => r.path === 'first').files;
  assert.match(files.find(f => f.path === 'code.txt').patch, /\+staged\n\+unstaged/);
  assert.match(files.find(f => f.path === 'committed.txt').patch, /branch commit/);
  assert.match(files.find(f => f.path === 'new file.txt').patch, /<script>/);
  assert.equal(files.find(f => f.path === 'binary.dat').omitted, 'binary');
  assert.equal(files.find(f => f.path === 'large.txt').omitted, 'large');
  assert.equal(files.find(f => f.path === 'link').omitted, 'symlink');
  assert.equal(files.some(f => f.path === 'ignored.txt'), false);
  assert.match(base.repositories.find(r => r.path === 'second').files[0].patch, /-before/);
  assert.equal((await read('head')).repositories.find(r => r.path === 'first').files.some(f => f.path === 'committed.txt'), false);
  assert.strictEqual(await read('base'), base);
  await assert.rejects(read('../../etc'), /invalid_comparison/);
});

test('real Git supports unborn repositories and worktrees without following discovery symlinks', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'workspace-diff-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const repo = await repository(root, 'repo');
  git(repo, 'worktree', 'add', '-qb', 'work', path.join(root, 'work'));
  await fs.writeFile(path.join(root, 'work', 'code.txt'), 'worktree edit\n');
  await fs.mkdir(path.join(root, 'unborn')); git(path.join(root, 'unborn'), 'init', '-b', 'main');
  await fs.writeFile(path.join(root, 'unborn', 'new.txt'), 'new\n');
  await fs.symlink(repo, path.join(root, 'outside'));
  const result = await createWorkspaceDiff(root)();
  assert.equal(result.repositories.length, 3);
  assert.match(result.repositories.find(r => r.path === 'work').files[0].patch, /worktree edit/);
  assert.match(result.repositories.find(r => r.path === 'unborn').files[0].patch, /\+new/);
});

test('HTTP route authenticates before reading Git and rejects mutation/ref injection', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  assert.equal((await requestHttp(harness, 'GET', '/agenthub/diff', {})).status, 401);
  const auth = { 'X-Agent-Token': 'correct-token' };
  assert.equal((await requestHttp(harness, 'POST', '/agenthub/diff', auth)).status, 405);
  assert.equal((await requestHttp(harness, 'GET', '/agenthub/diff?comparison=--output=/tmp/out', auth)).status, 400);
});

test('bounded real Git inventory marks omitted files instead of silently presenting a complete review', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'workspace-diff-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const dir = await repository(root, 'repo');
  await Promise.all(Array.from({ length: 205 }, (_, i) => fs.writeFile(path.join(dir, `file-${i}.txt`), 'new\n')));
  const result = await createWorkspaceDiff(root)();
  assert.equal(result.truncated, true);
  assert.equal(result.repositories[0].files.length, 200);
});

test('real common server serves authenticated Git patches over TCP HTTP', { timeout: 15000 }, async t => {
  const { once } = require('node:events');
  const { createCommonServer } = require('../../common/server');
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'workspace-diff-http-'));
  const dir = await repository(root, 'repo');
  await fs.writeFile(path.join(dir, 'code.txt'), 'live edit\n');
  let child;
  const runtime = createCommonServer({
    env: { PATH: process.env.PATH, HOME: root, AGENTHUB_WORKDIR: root, AGENTHUB_WORKSPACE_ROOT: root,
      AGENTHUB_PORT: '0', AGENTHUB_SESSION_ID: 'fixture', AGENTHUB_MODE: 'interactive',
      AGENTHUB_RESUME: '0', AGENTHUB_PROMPT: '', AGENTHUB_SKILLS_SYNC_SECONDS: '0',
      AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:1', AGENTHUB_CALLBACK_TOKEN: 'fixture-token', AGENTHUB_FILE_ROOT: path.join(root, 'files') },
    driver: { name: 'Fixture', stateDir: '.fixture', authFilename: 'auth.json',
      attachmentCapabilities: { nativeImages: false, localImagePaths: false, mcpImages: false },
      prepare() {}, buildCommand: () => ({ cmd: process.execPath, args: ['-e', 'process.stdin.resume()'], pipe: true }),
      isResumeCommand: () => false, isMissingResume: () => false },
    dependencies: { pty: {}, spawn: (cmd, args, options) => { child = require('node:child_process').spawn(cmd, args, options); return child; },
      process: { on() {}, exit() {} }, setInterval: () => ({ unref() {} }) }
  });
  t.after(async () => {
    runtime.webSocketServer.close(); runtime.httpServer.closeAllConnections();
    await new Promise(resolve => runtime.httpServer.close(resolve));
    if (child && child.exitCode === null) { child.removeAllListeners('exit'); const stopped = once(child, 'exit'); child.kill(); await stopped; }
    await fs.rm(root, { recursive: true, force: true });
  });
  if (!runtime.httpServer.listening) await once(runtime.httpServer, 'listening');
  const url = `http://127.0.0.1:${runtime.httpServer.address().port}/agenthub/diff`;
  assert.equal((await fetch(url)).status, 401);
  const response = await fetch(url, { headers: { 'X-Agent-Token': 'fixture-token' } });
  assert.equal(response.status, 200); assert.equal(response.headers.get('cache-control'), 'no-store');
  assert.match((await response.json()).repositories[0].files[0].patch, /\+live edit/);
});
