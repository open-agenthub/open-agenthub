'use strict';

// The session's git store and per-host authors (docs/git-credentials.md, issue #132), against the
// real git binary in a throwaway HOME: what matters is what `git credential fill` and the author
// of a commit turn out to be, not which files were written.

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const test = require('node:test');

const gitIdentity = require('../../common/git-identity');
const { createHarness, requestHttp } = require('./common-server-harness');

const setupCliAuth = path.join(__dirname, '..', '..', 'common', 'setup-cli-auth.sh');

function freshHome() {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'git-identity-'));
  const env = { ...process.env, HOME: home, GIT_CONFIG_NOSYSTEM: '1' };
  // What the entrypoint does before: the owner's stored identity as the global fallback.
  execFileSync('git', ['config', '--global', 'user.name', 'Owner'], { env });
  execFileSync('git', ['config', '--global', 'user.email', 'owner@example.com'], { env });
  return { home, env };
}

function authorIn(home, env, remote) {
  const repo = fs.mkdtempSync(path.join(home, 'repo-'));
  execFileSync('git', ['init', '-q', repo], { env });
  execFileSync('git', ['-C', repo, 'remote', 'add', 'origin', remote], { env });
  return execFileSync('git', ['-C', repo, 'var', 'GIT_AUTHOR_IDENT'], { env, encoding: 'utf8' }).replace(/>.*$/s, '>');
}

function fill(env, host) {
  return execFileSync('git', ['credential', 'fill'], {
    env: { ...env, GIT_TERMINAL_PROMPT: '0' }, input: `protocol=https\nhost=${host}\n\n`, encoding: 'utf8'
  });
}

test('the chosen token answers for its host and its author signs commits to that host only', () => {
  const { home, env } = freshHome();
  gitIdentity.install({
    home,
    credentials: 'https://oauth2:glpat-group@gitlab.example.com\nhttps://x-access-token:ghp_personal@github.com\n',
    authors: [{ host: 'gitlab.example.com', name: 'Platform Bot', email: 'platform-bot@example.com' }]
  }, { setupCliAuth });

  assert.match(fill(env, 'gitlab.example.com'), /password=glpat-group/);
  assert.equal(fs.statSync(path.join(home, '.git-credentials')).mode & 0o777, 0o600);
  for (const remote of ['https://gitlab.example.com/platform/app.git', 'git@gitlab.example.com:platform/app.git',
    'ssh://git@gitlab.example.com/platform/app.git', 'git@gitlab.example.com:app.git']) {
    assert.equal(authorIn(home, env, remote), 'Platform Bot <platform-bot@example.com>', remote);
  }
  // Another host, and a host that only starts with the same name, keep the owner.
  assert.equal(authorIn(home, env, 'https://github.com/someone/app.git'), 'Owner <owner@example.com>');
  assert.equal(authorIn(home, env, 'https://gitlab.example.com.evil.example/x.git'), 'Owner <owner@example.com>');
  // gh/glab follow the store, as at start.
  assert.match(fs.readFileSync(path.join(home, '.config', 'glab-cli', 'config.yml'), 'utf8'), /token: glpat-group/);
});

test('switching to a token without an author drops the previous identity, and installing twice adds one include', () => {
  const { home, env } = freshHome();
  const store = 'https://oauth2:glpat-group@gitlab.example.com\n';
  gitIdentity.install({ home, credentials: store,
    authors: [{ host: 'gitlab.example.com', name: 'Platform Bot', email: 'platform-bot@example.com' }] }, { setupCliAuth });
  gitIdentity.install({ home, credentials: 'https://oauth2:glpat-personal@gitlab.example.com\n', authors: [] }, { setupCliAuth });

  assert.equal(authorIn(home, env, 'https://gitlab.example.com/platform/app.git'), 'Owner <owner@example.com>');
  assert.match(fill(env, 'gitlab.example.com'), /password=glpat-personal/);
  const includes = execFileSync('git', ['config', '--global', '--get-all', 'include.path'], { env, encoding: 'utf8' });
  assert.equal(includes.trim().split('\n').length, 1);
  assert.match(fs.readFileSync(path.join(home, '.config', 'glab-cli', 'config.yml'), 'utf8'), /token: glpat-personal/);
  assert.doesNotMatch(fs.readFileSync(path.join(home, '.config', 'glab-cli', 'config.yml'), 'utf8'), /glpat-group/);
});

test('an empty store removes the file and the CLI configs derived from it', () => {
  const { home, env } = freshHome();
  gitIdentity.install({ home, credentials: 'https://oauth2:glpat-group@gitlab.example.com\n', authors: [] }, { setupCliAuth });
  gitIdentity.install({ home, credentials: '', authors: [] }, { setupCliAuth });

  assert.equal(fs.existsSync(path.join(home, '.git-credentials')), false);
  assert.equal(fs.existsSync(path.join(home, '.config', 'glab-cli', 'config.yml')), false);
  // Nothing answers any more, so git (with prompts off) gives up instead of printing a password.
  const result = require('node:child_process').spawnSync('git', ['credential', 'fill'], {
    env: { ...env, GIT_TERMINAL_PROMPT: '0' }, input: 'protocol=https\nhost=gitlab.example.com\n\n', encoding: 'utf8' });
  assert.notEqual(result.status, 0);
  assert.doesNotMatch(result.stdout, /password=/);
});

test('the payload is refused when a value could end a git config value or add a store line', () => {
  const bad = [
    '[]',
    '{"credentials":"https://oauth2:t@gitlab.example.com\\nnot-a-store-line"}',
    '{"credentials":"https://oauth2:t@gitlab.example.com/path"}',
    '{"authors":[{"host":"gitlab.example.com","name":"Bot\\"\\n[core]\\n\\tsshCommand = evil"}]}',
    '{"authors":[{"host":"gitlab.example.com/**","name":"Bot"}]}',
    '{"authors":[{"host":"gitlab.example.com","email":"two words@example.com"}]}',
    '{"authors":[{"host":"gitlab.example.com"}]}',
    'not json'
  ];
  for (const body of bad) assert.throws(() => gitIdentity.parsePayload(body), /invalid_git/, body);
  assert.deepEqual(gitIdentity.parsePayload('{"credentials":null}'), { credentials: '', authors: [] });
});

test('the entrypoint form installs authors from the secret file and leaves the store alone', () => {
  const { home, env } = freshHome();
  const authorsFile = path.join(home, 'authors');
  fs.writeFileSync(authorsFile, JSON.stringify([{ host: 'gitlab.example.com', name: 'Platform Bot', email: 'bot@example.com' }]));
  execFileSync(process.execPath, [path.join(__dirname, '..', '..', 'common', 'git-identity.js'), authorsFile], { env });

  assert.equal(authorIn(home, env, 'https://gitlab.example.com/a/b.git'), 'Platform Bot <bot@example.com>');
  assert.equal(fs.existsSync(path.join(home, '.git-credentials')), false);
  // No file at all (a session without PATs) is not an error.
  execFileSync(process.execPath, [path.join(__dirname, '..', '..', 'common', 'git-identity.js'), path.join(home, 'missing')], { env });
});

// ---- PUT /agenthub/git-credentials -------------------------------------------------------------

const headers = { 'X-Agent-Token': 'correct-token' };

test('git credential route checks the token, the method and the payload before installing', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });

  const missing = await requestHttp(harness, 'PUT', '/agenthub/git-credentials', {}, '{}');
  const get = await requestHttp(harness, 'GET', '/agenthub/git-credentials', headers);
  const bad = await requestHttp(harness, 'PUT', '/agenthub/git-credentials', headers,
    '{"credentials":"https://oauth2:t@evil.example\\nhttps://x"}');

  assert.deepEqual([missing.status, get.status, bad.status], [401, 405, 400]);
  assert.equal(harness.gitInstalls.length, 0);
});

test('git credential route installs into HOME without restarting the agent', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  const body = JSON.stringify({
    credentials: 'https://oauth2:glpat-group@gitlab.example.com\n',
    authors: [{ host: 'gitlab.example.com', name: 'Platform Bot' }]
  });

  const response = await requestHttp(harness, 'PUT', '/agenthub/git-credentials', headers, body);

  assert.equal(response.status, 200);
  assert.deepEqual(harness.gitInstalls, [{
    home: '/home/agent',
    credentials: 'https://oauth2:glpat-group@gitlab.example.com\n',
    authors: [{ host: 'gitlab.example.com', name: 'Platform Bot' }]
  }]);
  assert.equal(harness.terminals[0].killed, false);
});
