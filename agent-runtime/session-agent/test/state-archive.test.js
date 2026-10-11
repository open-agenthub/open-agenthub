'use strict';

// Runs the real archive, signature and restore commands against a real directory. The server
// tests only see these commands as strings handed to a fake exec, which cannot tell whether tar
// accepts the options, whether the excludes of the two commands agree, or whether what the
// entrypoint runs on resume can read what the server wrote.

const assert = require('node:assert/strict');
const { execFile } = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { signatureCommand, archiveCommand } = require('../../common/state-archive');

const driver = {
  stateDir: '.test-agent',
  authFilename: 'auth.json',
  stateExcludes: ['.test-agent/cache', '.test-agent/agents/*/agent.sqlite']
};

function shell(command) {
  return new Promise(resolve => execFile('/bin/sh', ['-c', command],
    (error, stdout) => resolve({ error, stdout: String(stdout) })));
}

function write(root, relative, content) {
  const file = path.join(root, relative);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, content);
}

function listing(root) {
  const found = {};
  const walk = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      if (entry.isDirectory()) walk(file);
      else found[path.relative(root, file)] = fs.readFileSync(file).toString('hex');
    }
  };
  walk(root);
  return found;
}

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'state-archive-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const home = path.join(root, 'home');
  write(home, '.test-agent/auth.json', '{"token":"synthetic"}');
  write(home, '.test-agent/projects/-workspace/conversation.jsonl', '{"turn":1}\n');
  write(home, '.test-agent/skills/own skill/SKILL.md', '# written in the session\n');
  write(home, '.test-agent/binary.bin', Buffer.from([0, 255, 10, 13, 0, 128]));
  write(home, '.test-agent/cache/large.bin', Buffer.alloc(64 * 1024, 7));
  write(home, '.test-agent/agents/main/agent.sqlite', 'excluded by a wildcard');
  write(home, '.test-agent/agents/main/notes.md', 'kept');
  write(home, 'outside.txt', 'not part of the state directory');
  return { root, home };
}

// The storage the presigned url points at: keeps the last body PUT to it.
function storage(t, status = 200) {
  const received = { bodies: [] };
  const server = http.createServer((request, response) => {
    const chunks = [];
    request.on('data', chunk => chunks.push(chunk));
    request.on('end', () => {
      if (status === 200) received.bodies.push(Buffer.concat(chunks));
      response.statusCode = status;
      response.end();
    });
  });
  t.after(() => server.close());
  return new Promise(resolve => server.listen(0, '127.0.0.1', () => {
    received.url = 'http://127.0.0.1:' + server.address().port + '/state.tgz';
    resolve(received);
  }));
}

// The entrypoint's restore, with its two fixed paths pointed into the fixture.
function restoreCommand(archive, home) {
  const entrypoint = fs.readFileSync(
    path.join(__dirname, '..', '..', 'common', 'entrypoint-common.sh'), 'utf8');
  const restore = 'tar xzf /tmp/state.tgz -C "$HOME" 2>/dev/null';
  assert.ok(entrypoint.includes(restore), 'the entrypoint restores with: ' + restore);
  return restore.replace('/tmp/state.tgz', '"' + archive + '"').replace('$HOME', home);
}

const expected = {
  '.test-agent/projects/-workspace/conversation.jsonl': Buffer.from('{"turn":1}\n').toString('hex'),
  '.test-agent/skills/own skill/SKILL.md': Buffer.from('# written in the session\n').toString('hex'),
  '.test-agent/binary.bin': '00ff0a0d0080',
  '.test-agent/agents/main/notes.md': Buffer.from('kept').toString('hex')
};

test('the archive the server uploads restores with the entrypoint command, minus the excludes', async t => {
  const { root, home } = fixture(t);
  const received = await storage(t);
  const archivePath = path.join(root, 'state.tgz');

  const run = await shell(archiveCommand({ home, driver, statePut: received.url, archivePath }));
  assert.equal(run.error, null);
  assert.equal(received.bodies.length, 1);
  assert.deepEqual(received.bodies[0], fs.readFileSync(archivePath));
  assert.ok(!fs.existsSync(archivePath + '.part'));

  const downloaded = path.join(root, 'downloaded.tgz');
  fs.writeFileSync(downloaded, received.bodies[0]);
  const restored = path.join(root, 'restored');
  fs.mkdirSync(restored);
  assert.equal((await shell(restoreCommand(downloaded, restored))).error, null);
  assert.deepEqual(listing(restored), expected);
});

test('an archive written by a runtime before the level change still restores', async t => {
  const { root, home } = fixture(t);
  const old = path.join(root, 'old.tgz');
  // What common/server.js ran up to 0.13.0.
  assert.equal((await shell('tar czf "' + old + '" -C "' + home + '" --exclude=".test-agent/auth.json" ' +
    '--exclude=".test-agent/cache" --exclude=".test-agent/agents/*/agent.sqlite" ".test-agent"')).error, null);
  const restored = path.join(root, 'restored');
  fs.mkdirSync(restored);
  assert.equal((await shell(restoreCommand(old, restored))).error, null);
  assert.deepEqual(listing(restored), expected);
});

test('a failed upload fails the run and keeps no side file', async t => {
  const { root, home } = fixture(t);
  const received = await storage(t, 503);
  const archivePath = path.join(root, 'state.tgz');
  const run = await shell(archiveCommand({ home, driver, statePut: received.url, archivePath }) + ' 2>/dev/null');
  assert.ok(run.error, 'the server must not remember this run as uploaded');
  assert.ok(!fs.existsSync(archivePath + '.part'));
});

test('a missing state directory fails the run and leaves the last good archive alone', async t => {
  const { root, home } = fixture(t);
  const received = await storage(t);
  const archivePath = path.join(root, 'state.tgz');
  fs.writeFileSync(archivePath, 'last good archive');
  const run = await shell(archiveCommand({
    home, driver: { ...driver, stateDir: '.missing' }, statePut: received.url, archivePath }));
  assert.ok(run.error);
  assert.equal(fs.readFileSync(archivePath, 'utf8'), 'last good archive');
  assert.equal(received.bodies.length, 0);
});

test('the signature is stable while nothing changes and across archive runs', async t => {
  const { root, home } = fixture(t);
  const received = await storage(t);
  const first = await shell(signatureCommand({ home, driver }));
  assert.match(first.stdout, /^[0-9a-f]{64}\b/);
  await shell(archiveCommand({
    home, driver, statePut: received.url, archivePath: path.join(root, 'state.tgz') }));
  assert.equal((await shell(signatureCommand({ home, driver }))).stdout, first.stdout);
});

test('the signature changes with every kind of change to archived state', async t => {
  const { home } = fixture(t);
  const signature = async () => (await shell(signatureCommand({ home, driver }))).stdout;
  let last = await signature();
  const changed = async what => {
    const next = await signature();
    assert.notEqual(next, last, what);
    last = next;
  };

  fs.appendFileSync(path.join(home, '.test-agent/projects/-workspace/conversation.jsonl'), '{"turn":2}\n');
  await changed('a file grew');
  // Same size, rewritten at once: the case a listing with whole-second timestamps misses.
  write(home, '.test-agent/agents/main/notes.md', 'KEPT');
  await changed('a file was rewritten to the same size within the same second');
  write(home, '.test-agent/skills/new/SKILL.md', '');
  await changed('an empty file appeared');
  fs.rmSync(path.join(home, '.test-agent/skills/new'), { recursive: true });
  await changed('a directory was removed');
  fs.renameSync(path.join(home, '.test-agent/binary.bin'), path.join(home, '.test-agent/binary2.bin'));
  await changed('a file was renamed');
  fs.chmodSync(path.join(home, '.test-agent/binary2.bin'), 0o600);
  await changed('a mode changed');
});

test('the signature ignores what the archive leaves out', async t => {
  const { home } = fixture(t);
  const before = (await shell(signatureCommand({ home, driver }))).stdout;
  write(home, '.test-agent/auth.json', '{"token":"rotated-synthetic"}');
  write(home, '.test-agent/cache/large.bin', 'rewritten');
  write(home, '.test-agent/cache/deeper/new.bin', 'new');
  write(home, '.test-agent/agents/main/agent.sqlite', 'rewritten by the agent');
  write(home, 'outside.txt', 'changed');
  assert.equal((await shell(signatureCommand({ home, driver }))).stdout, before);
});

test('the signature of an unreadable or missing state directory differs from a readable one', async t => {
  const { home } = fixture(t);
  const readable = (await shell(signatureCommand({ home, driver }))).stdout;
  const missing = (await shell(signatureCommand({ home, driver: { ...driver, stateDir: '.missing' } }))).stdout;
  assert.notEqual(missing, readable);
  assert.equal((await shell(signatureCommand({ home: path.join(home, 'nowhere'), driver }))).stdout, '');
});
