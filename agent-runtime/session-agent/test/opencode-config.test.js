'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const userConfig = require('../../opencode/user-config');
const { managedConfig, writeManagedConfig } = require('../../opencode/managed-config');

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'opencode-config-'));
}

test('managed config registers the policy plugin and hands approval to it', () => {
  const config = managedConfig('/opt/agenthub/session-agent/opencode');
  assert.deepEqual(config.plugin, ['file:///opt/agenthub/session-agent/opencode/policy-plugin.mjs']);
  assert.equal(config.permission, 'allow');
  assert.equal(config.autoupdate, false);
  assert.equal(config.share, 'disabled');
  assert.throws(() => managedConfig('relative/path'), /absolute/);

  const out = path.join(tempDir(), 'etc', 'opencode.json');
  writeManagedConfig(out, '/opt/session-agent/opencode');
  assert.deepEqual(JSON.parse(fs.readFileSync(out, 'utf8')).plugin,
    ['file:///opt/session-agent/opencode/policy-plugin.mjs']);
});

test('MCP servers are converted to OpenCode local and remote entries', () => {
  const mcp = userConfig.convertMcp({
    files: { command: 'node', args: ['/opt/files.mjs'], env: { TOKEN: '${HUB_TOKEN}' } },
    docs: { type: 'http', url: 'https://mcp.example.com/mcp', headers: { Authorization: 'Bearer ${DOCS_TOKEN}' } },
    old: { type: 'sse', url: 'http://mcp.example.com/sse', enabled: false }
  });
  assert.deepEqual(mcp.files, {
    type: 'local', command: ['node', '/opt/files.mjs'], environment: { TOKEN: '{env:HUB_TOKEN}' }, enabled: true
  });
  assert.deepEqual(mcp.docs, {
    type: 'remote', url: 'https://mcp.example.com/mcp', headers: { Authorization: 'Bearer {env:DOCS_TOKEN}' }, enabled: true
  });
  assert.equal(mcp.old.enabled, false);
});

test('an unconvertible MCP server is skipped instead of failing the session', () => {
  const mcp = userConfig.convertMcp({
    'bad name': { command: 'x' },
    both: { command: 'x', url: 'https://mcp.example.com' },
    ftp: { url: 'ftp://mcp.example.com' },
    ok: { command: 'node' }
  });
  assert.deepEqual(Object.keys(mcp), ['ok']);
});

test('Go model selection prefers the list, then any paid model the catalogue offers', () => {
  const catalog = 'opencode-go/space-bunny-free\nopencode-go/kimi-k3\nopencode-go/glm-5.3\n';
  assert.equal(userConfig.selectGoModel(catalog), 'opencode-go/glm-5.3');
  assert.equal(userConfig.selectGoModel('opencode-go/space-bunny-free\nopencode-go/new-model\n'),
    'opencode-go/new-model');
  assert.equal(userConfig.selectGoModel(''), null);
});

test('only an OpenCode Go key or login gets a Go default model', () => {
  assert.equal(userConfig.goProviderFor({ AGENTHUB_AUTH_MODE: 'apikey', OPENCODE_API_KEY: 'k' }), 'opencode-go');
  assert.equal(userConfig.goProviderFor({ AGENTHUB_AUTH_MODE: 'subscription' },
    { 'opencode-go': { type: 'api', key: 'k' } }), 'opencode-go');
  // A login for any other provider is left to OpenCode, which picks among what is logged in.
  assert.equal(userConfig.goProviderFor({ AGENTHUB_AUTH_MODE: 'subscription' },
    { anthropic: { type: 'oauth' } }), null);
});

test('the default model honours the override and survives a failing catalogue', () => {
  const env = { AGENTHUB_AUTH_MODE: 'apikey', OPENCODE_API_KEY: 'k' };
  assert.equal(userConfig.defaultModel({ ...env, AGENTHUB_OPENCODE_MODEL: 'opencode-go/kimi-k3' }, null,
    () => { throw new Error('not called'); }), 'opencode-go/kimi-k3');
  assert.equal(userConfig.defaultModel(env, null, () => 'opencode-go/glm-5.3\n'), 'opencode-go/glm-5.3');
  assert.equal(userConfig.defaultModel(env, null, () => { throw new Error('offline'); }), null);
});

test('user config lists the instructions file only while it exists', () => {
  const dir = tempDir();
  const instructions = path.join(dir, 'agenthub-session.md');
  const env = { AGENTHUB_AUTH_MODE: 'subscription', AGENTHUB_OPENCODE_INSTRUCTIONS: instructions };
  assert.equal(userConfig.userConfig(env).instructions, undefined);
  fs.writeFileSync(instructions, 'be brief');
  assert.deepEqual(userConfig.userConfig(env).instructions, [instructions]);
});
