'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const bashPath = process.platform === 'win32'
  ? 'C:\\Program Files\\Git\\bin\\bash.exe'
  : 'bash';
const scriptPath = path.join(__dirname, '..', '..', 'common', 'setup-cli-auth.sh');

function toBashPath(windowsPath) {
  if (process.platform !== 'win32') return windowsPath;
  return windowsPath.replace(/^([A-Za-z]):\\/, (_, drive) => `/${drive.toLowerCase()}/`).replace(/\\/g, '/');
}

function runScript(credentials) {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'cli-auth-'));
  if (credentials !== null) {
    fs.writeFileSync(path.join(home, '.git-credentials'), credentials);
  }
  const result = spawnSync(bashPath, [toBashPath(scriptPath)], {
    env: { ...process.env, HOME: toBashPath(home) },
    encoding: 'utf8'
  });
  return { home, result };
}

function readIfExists(file) {
  return fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
}

test('writes gh hosts.yml for GitHub-style credentials', () => {
  const { home, result } = runScript('https://x-access-token:gho_abc123@github.com\n');
  assert.equal(result.status, 0, result.stderr);

  const hosts = readIfExists(path.join(home, '.config', 'gh', 'hosts.yml'));
  assert.ok(hosts, 'hosts.yml missing');
  assert.match(hosts, /^github\.com:/m);
  assert.match(hosts, /oauth_token: gho_abc123/);
  assert.match(hosts, /git_protocol: https/);
  assert.equal(readIfExists(path.join(home, '.config', 'glab-cli', 'config.yml')), null);
});

test('writes glab config.yml for GitLab-style credentials', () => {
  const { home, result } = runScript('https://oauth2:glpat-xyz@gitlab.example.com\n');
  assert.equal(result.status, 0, result.stderr);

  const config = readIfExists(path.join(home, '.config', 'glab-cli', 'config.yml'));
  assert.ok(config, 'config.yml missing');
  assert.match(config, /^hosts:/m);
  assert.match(config, /gitlab\.example\.com:/);
  assert.match(config, /token: glpat-xyz/);
  assert.equal(readIfExists(path.join(home, '.config', 'gh', 'hosts.yml')), null);
});

test('handles multiple hosts of both kinds', () => {
  const { home, result } = runScript([
    'https://x-access-token:gho_one@github.com',
    'https://x-access-token:ghe_two@github.corp.example',
    'https://oauth2:glpat-three@gitlab.com',
    ''
  ].join('\n'));
  assert.equal(result.status, 0, result.stderr);

  const hosts = readIfExists(path.join(home, '.config', 'gh', 'hosts.yml'));
  assert.match(hosts, /github\.com:/);
  assert.match(hosts, /github\.corp\.example:/);
  assert.match(hosts, /oauth_token: gho_one/);
  assert.match(hosts, /oauth_token: ghe_two/);

  const config = readIfExists(path.join(home, '.config', 'glab-cli', 'config.yml'));
  assert.match(config, /gitlab\.com:/);
  assert.match(config, /token: glpat-three/);
});

test('percent-decodes escaped tokens', () => {
  const { home, result } = runScript('https://oauth2:glpat%2Dabc%3Ddef@gitlab.com\n');
  assert.equal(result.status, 0, result.stderr);

  const config = readIfExists(path.join(home, '.config', 'glab-cli', 'config.yml'));
  assert.match(config, /token: glpat-abc=def/);
});

test('ignores malformed lines and unknown credential users', () => {
  const { home, result } = runScript([
    'not-a-url',
    'https://nouserinfo.example.com',
    'https://someone:tok@bitbucket.example.com',
    ''
  ].join('\n'));
  assert.equal(result.status, 0, result.stderr);

  assert.equal(readIfExists(path.join(home, '.config', 'gh', 'hosts.yml')), null);
  assert.equal(readIfExists(path.join(home, '.config', 'glab-cli', 'config.yml')), null);
});

test('exits cleanly when no credential store exists', () => {
  const { result } = runScript(null);
  assert.equal(result.status, 0, result.stderr);
});
