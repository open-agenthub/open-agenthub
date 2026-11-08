import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createRequire } from 'node:module';
const { inspect } = createRequire(import.meta.url)('../../agent-runtime/common/repository-checkpoint.js');
const root = await fs.mkdtemp(path.join(os.tmpdir(), 'repository-proof-'));
const source = path.join(root, 'source'), bare = path.join(root, 'remote.git'), workspace = path.join(root, 'workspace');
await fs.mkdir(source); await fs.mkdir(workspace);
const exec = promisify(execFile);
const git = async (cwd, ...args) => (await exec('git', args, { cwd, encoding: 'utf8' })).stdout.trim();
let requireAuth = false;
const server = http.createServer(async (request, response) => {
  if (requireAuth && request.headers.authorization !== 'Basic ' + Buffer.from('proof:synthetic-fetch').toString('base64')) {
    response.writeHead(401, { 'www-authenticate': 'Basic realm="proof"' }).end(); return;
  }
  // A read-only dumb HTTP remote proves fetchability needs no receive-pack permission.
  const name = new URL(request.url, 'http://example.com').pathname;
  const file = path.resolve(bare, '.' + name);
  if (!file.startsWith(bare + path.sep)) { response.writeHead(403).end(); return; }
  try { response.end(await fs.readFile(file)); } catch { response.writeHead(404).end(); }
});
try {
  await git(source, 'init', '-qb', 'work/proof');
  await git(source, 'config', 'user.name', 'Proof'); await git(source, 'config', 'user.email', 'proof@example.com');
  await fs.writeFile(path.join(source, 'code.txt'), 'committed\n');
  await fs.writeFile(path.join(source, '.gitignore'), '.env\nnode_modules/\n');
  await git(source, 'add', '.'); await git(source, 'commit', '-qm', 'Proof baseline');
  const commit = await git(source, 'rev-parse', 'HEAD');
  await git(root, 'clone', '--bare', source, bare); await git(bare, 'update-server-info');
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}`;
  await git(workspace, 'clone', url, 'repo');
  const repo = path.join(workspace, 'repo');
  const configured = [{ destination: 'repo', url }];
  const check = async () => (await inspect(workspace, configured)).repositories[0];
  let result = await check(); assert.equal(result.error, null); assert.equal(result.commit, commit);
  assert.equal(result.branch, 'work/proof'); assert.equal(result.unpushed, 0);
  await fs.writeFile(path.join(repo, '.env'), 'PRIVATE_PROOF_VALUE\n');
  result = await check(); assert.equal(result.error, null); assert.equal(result.ignored.count, 1);
  assert.deepEqual(result.ignored.names, ['.env']); assert.ok(!JSON.stringify(result).includes('PRIVATE_PROOF_VALUE'));
  await fs.writeFile(path.join(repo, 'new.txt'), 'not saved\n');
  result = await check(); assert.equal(result.error, 'repository_work_not_saved'); assert.equal(result.untracked, 1);
  await fs.rm(path.join(repo, 'new.txt'));
  await fs.writeFile(path.join(repo, 'code.txt'), 'dirty\n');
  result = await check(); assert.equal(result.error, 'repository_work_not_saved'); assert.equal(result.trackedChanges, 1);
  await git(repo, 'add', 'code.txt'); result = await check(); assert.equal(result.error, 'repository_work_not_saved');
  await fs.writeFile(path.join(repo, 'code.txt'), 'committed\n');
  assert.equal(await git(repo, 'diff', 'HEAD', '--name-only'), '');
  assert.equal((await check()).error, 'repository_work_not_saved');
  await fs.writeFile(path.join(repo, 'code.txt'), 'dirty\n');
  await git(repo, 'config', 'user.name', 'Proof'); await git(repo, 'config', 'user.email', 'proof@example.com');
  await git(repo, 'commit', '-qm', 'Unpublished proof change');
  result = await check(); assert.equal(result.error, 'repository_commit_not_pushed'); assert.equal(result.unpushed, 1);
  await git(repo, 'reset', '--hard', commit);
  await git(repo, 'checkout', '--detach', commit); result = await check(); assert.ok(result.error);
  await git(repo, 'checkout', 'work/proof');
  requireAuth = true;
  assert.equal((await check()).error, 'repository_check_failed');
  const privateHome = path.join(root, 'source-home'); await fs.mkdir(privateHome);
  await fs.writeFile(path.join(privateHome, '.netrc'), 'machine 127.0.0.1 login proof password synthetic-fetch\n', { mode: 0o600 });
  const oldHome = process.env.HOME;
  try { process.env.HOME = privateHome; assert.equal((await check()).error, 'repository_check_failed'); }
  finally { if (oldHome === undefined) delete process.env.HOME; else process.env.HOME = oldHome; }
  assert.equal((await inspect(workspace, [{ destination: 'repo', url: 'ssh://git@example.com/repo' }])).repositories[0].error, 'repository_ssh_unsupported');
  const credentials = url.replace('http://', 'http://proof:synthetic-fetch@') + '\n';
  const authenticated = await inspect(workspace, configured, credentials);
  assert.equal(authenticated.repositories[0].error, null);
  const oddScratch = path.join(root, "scratch space'quote"); await fs.mkdir(oddScratch);
  const oldTmp = process.env.TMPDIR;
  try { process.env.TMPDIR = oddScratch; assert.equal((await inspect(workspace, configured, credentials)).repositories[0].error, null); }
  finally { if (oldTmp === undefined) delete process.env.TMPDIR; else process.env.TMPDIR = oldTmp; }

  assert(!JSON.stringify(authenticated).includes('synthetic-fetch'));
  const oldGitDir = process.env.GIT_DIR;
  try { process.env.GIT_DIR = bare; assert.equal((await inspect(workspace, configured, credentials)).repositories[0].error, null); }
  finally { if (oldGitDir === undefined) delete process.env.GIT_DIR; else process.env.GIT_DIR = oldGitDir; }

  await git(repo, 'update-index', '--assume-unchanged', 'code.txt');
  result = (await inspect(workspace, configured, credentials)).repositories[0];
  assert.equal(result.error, 'repository_index_flags_unsupported');
  await git(repo, 'update-index', '--no-assume-unchanged', 'code.txt');
  await git(repo, 'remote', 'remove', 'origin'); result = await check(); assert.ok(result.error);
  assert.equal((await inspect(workspace, [{ destination: '../escape', url }]).catch(error => error)).message, 'repository_path_invalid');
  console.log('PASS: real read-only HTTP Git, exact commit/branch, ignored files allowed without content, tracked/staged/untracked/unpublished/detached/missing-remote/path refusals.');
} finally {
  server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  await fs.rm(root, { recursive: true, force: true });
}
