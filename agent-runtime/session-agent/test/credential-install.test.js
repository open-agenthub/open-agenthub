'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const install = require('../../common/credential-install');

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'credential-install-'));
}

test('baseline file lives under HOME/.agenthub and round-trips a sha256 only', () => {
  const home = tempDir();
  const file = install.baselineFile({ HOME: home });
  assert.equal(file, path.join(home, '.agenthub', 'credential-baseline'));
  assert.equal(install.readBaselineHash(file), null);

  const hash = install.sha256(Buffer.from('x'));
  install.writeBaselineHash(file, hash);
  assert.equal(install.readBaselineHash(file), hash);

  fs.writeFileSync(file, 'not a hash\n');
  assert.equal(install.readBaselineHash(file), null);
  assert.throws(() => install.writeBaselineHash(file, 'nope'), /sha256/);
});

// The poll can land between the agent writing the baseline and writing the credential. With a
// single "last uploaded" value the old file would then look like a change and be uploaded.
test('known hashes keep both the last upload and an adopted baseline', () => {
  const home = tempDir();
  const file = install.baselineFile({ HOME: home });
  const old = install.sha256(Buffer.from('old'));
  const fresh = install.sha256(Buffer.from('fresh'));
  const known = new install.KnownHashes(old);

  install.writeBaselineHash(file, fresh);
  assert.equal(known.adopt(file), true);
  assert.equal(known.adopt(file), false);
  assert.equal(known.has(old), true);
  assert.equal(known.has(fresh), true);
  assert.equal(known.has(install.sha256(Buffer.from('rotated'))), false);
});

test('known hashes are bounded', () => {
  const known = new install.KnownHashes();
  for (let i = 0; i < install.MAX_KNOWN_HASHES + 5; i++) known.add(install.sha256(Buffer.from('h' + i)));
  assert.equal(known.values.size, install.MAX_KNOWN_HASHES);
  assert.equal(known.has(install.sha256(Buffer.from('h0'))), false);
  assert.equal(known.has(install.sha256(Buffer.from('h' + (install.MAX_KNOWN_HASHES + 4)))), true);
});

test('writeCredentialFile creates the directory, writes 0600 and leaves no temp file behind', () => {
  const home = tempDir();
  const target = path.join(home, '.provider', 'auth.json');
  install.writeCredentialFile(target, Buffer.from('{"a":1}'));

  assert.equal(fs.readFileSync(target, 'utf8'), '{"a":1}');
  assert.deepEqual(fs.readdirSync(path.dirname(target)), ['auth.json']);
  if (process.platform !== 'win32') assert.equal(fs.statSync(target).mode & 0o777, 0o600);
});

test('credentialTarget is the driver path or HOME/<stateDir>/<authFilename>, never outside HOME', () => {
  const env = { HOME: '/home/agent' };
  const plain = { stateDir: '.provider', authFilename: 'auth.json' };
  assert.equal(install.credentialTarget(env, plain), path.resolve('/home/agent', '.provider', 'auth.json'));

  const custom = { ...plain, credentialPath: () => '/home/agent/.config/provider/auth.json' };
  assert.equal(install.credentialTarget(env, custom), path.resolve('/home/agent/.config/provider/auth.json'));

  const escaping = { ...plain, credentialPath: () => '/etc/passwd' };
  assert.throws(() => install.credentialTarget(env, escaping), /escapes/);
  const traversing = { ...plain, credentialPath: () => '/home/agent/../../etc/x' };
  assert.throws(() => install.credentialTarget(env, traversing), /escapes/);
});

test('looksLikeJsonObject accepts only a bounded JSON object', () => {
  assert.equal(install.looksLikeJsonObject(Buffer.from('{"a":1}')), true);
  assert.equal(install.looksLikeJsonObject(Buffer.from('[1]')), false);
  assert.equal(install.looksLikeJsonObject(Buffer.from('null')), false);
  assert.equal(install.looksLikeJsonObject(Buffer.from('nope')), false);
  assert.equal(install.looksLikeJsonObject(Buffer.alloc(0)), false);
  assert.equal(install.looksLikeJsonObject(Buffer.alloc(install.MAX_CREDENTIAL_BYTES + 1, 0x20)), false);
});
