'use strict';

// The session agent masks a session's secret values in what it uploads (docs/session-secrets.md).
// Run against a real directory laid out like a Kubernetes secret volume.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createSecretMask } = require('../../common/secret-mask');

function secretVolume(files) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'secrets-'));
  const stamp = path.join(dir, '..2026_10_17_12_00_00.000000001');
  fs.mkdirSync(stamp);
  for (const [name, value] of Object.entries(files)) fs.writeFileSync(path.join(stamp, name), value);
  fs.symlinkSync(path.basename(stamp), path.join(dir, '..data'));
  for (const name of Object.keys(files)) fs.symlinkSync('..data/' + name, path.join(dir, name));
  return dir;
}

test('masks file and env values in raw, JSON-escaped and terminal form', () => {
  const pem = '-----BEGIN KEY-----\nMIIEvQIBADANBgkqhkiG9w0B\n-----END KEY-----\n';
  const dir = secretVolume({ API_TOKEN: 'tok-0123456789', 'key.pem': pem });
  const mask = createSecretMask({ env: { AGENTHUB_SECRETS_DIR: dir, AGENTHUB_SECRET_ENV: 'DEPLOY', DEPLOY: 'va"lue-of-env' } });

  assert.equal(mask.refresh() > 0, true);
  assert.equal(mask.mask('use tok-0123456789 now'), 'use [secret:API_TOKEN] now');
  assert.equal(mask.mask(JSON.stringify({ t: 'x va"lue-of-env' })), '{"t":"x [secret:DEPLOY]"}');
  assert.doesNotMatch(mask.mask(pem.replace(/\n/g, '\r\n')), /MIIEvQ/);
  assert.doesNotMatch(mask.mask(JSON.stringify(pem)), /MIIEvQ/);
});

test('ignores the kubelet bookkeeping entries and picks up a changed file on refresh', () => {
  const dir = secretVolume({ API_TOKEN: 'first-value-123' });
  const mask = createSecretMask({ env: { AGENTHUB_SECRETS_DIR: dir } });
  mask.refresh();
  assert.equal(mask.mask('first-value-123'), '[secret:API_TOKEN]');

  // What the kubelet does on an update: a new timestamped directory, then "..data" swapped.
  const next = path.join(dir, '..2026_10_17_12_05_00.000000002');
  fs.mkdirSync(next);
  fs.writeFileSync(path.join(next, 'API_TOKEN'), 'second-value-456');
  fs.renameSync(path.join(dir, '..data'), path.join(dir, '..data_old'));
  fs.symlinkSync(path.basename(next), path.join(dir, '..data'));
  mask.refresh();
  assert.equal(mask.mask('second-value-456'), '[secret:API_TOKEN]');
});

test('leaves short values, a missing directory and no secrets alone', () => {
  const mask = createSecretMask({ env: { AGENTHUB_SECRETS_DIR: '/does/not/exist', AGENTHUB_SECRET_ENV: 'PIN', PIN: '1234' } });
  assert.equal(mask.refresh(), 0);
  assert.equal(mask.active(), false);
  assert.equal(mask.mask('pin 1234'), 'pin 1234');
});

test('masks callback and inline MCP credentials including standalone URL userinfo', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mcp-mask-'));
  const file = path.join(dir, 'mcp.json');
  const config = { mcpServers: { custom: {
    command: 'ordinary-command', args: ['ordinary-argument'],
    headers: { Authorization: 'Bearer header-token-434', Custom: 'custom-header-434' },
    env: { CUSTOM: 'quoted-"credential-434', SHORT: '1234' },
    url: 'https://username-434:password%2F434@example.com/mcp?key=query%2Fvalue434&next=query+space434'
  } } };
  fs.writeFileSync(file, JSON.stringify(config));
  const mask = createSecretMask({ env: { AGENTHUB_CALLBACK_TOKEN: 'callback-token-434', AGENTHUB_MCP_CONFIG: file } });
  mask.refresh();
  for (const value of ['callback-token-434', 'header-token-434', 'custom-header-434',
    'quoted-"credential-434', 'username-434', 'password%2F434', 'password/434', 'query%2Fvalue434', 'query/value434', 'query+space434', 'query space434']) {
    assert.ok(!mask.mask(value).includes(value), value);
    assert.ok(!mask.mask(JSON.stringify(value)).includes(JSON.stringify(value).slice(1, -1)), value);
  }
  assert.equal(mask.mask('ordinary-command ordinary-argument 1234'), 'ordinary-command ordinary-argument 1234');
  fs.writeFileSync(file, JSON.stringify({ mcpServers: { next: { env: { TOKEN: 'rotated-token-434' } } } }));
  mask.refresh();
  assert.equal(mask.mask('rotated-token-434'), '[secret:MCP]');
  fs.writeFileSync(file, '{invalid');
  assert.doesNotThrow(() => mask.refresh());
  assert.equal(mask.mask('callback-token-434'), '[secret:AGENTHUB_CALLBACK_TOKEN]');
  fs.rmSync(dir, { recursive: true, force: true });
});
