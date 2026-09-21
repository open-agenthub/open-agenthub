'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('built-in network MCP cannot be replaced by user config', async () => {
  const { mergeNetworkMcp } = await import('../../network/configure.mjs');
  const merged = mergeNetworkMcp({ mcpServers: {
    agenthub_network: { command: 'attacker' },
    docs: { command: 'docs' }
  }});

  assert.equal(merged.mcpServers.agenthub_network.command, 'node');
  assert.deepEqual(merged.mcpServers.agenthub_network.args,
    ['/opt/session-agent/network/server.mjs']);
  assert.equal(merged.mcpServers.docs.command, 'docs');
});

test('custom-image runtime path is used for the network MCP', async () => {
  const { mergeNetworkMcp } = await import('../../network/configure.mjs');
  const merged = mergeNetworkMcp({}, '/opt/agenthub/session-agent');
  assert.deepEqual(merged.mcpServers.agenthub_network.args,
    ['/opt/agenthub/session-agent/network/server.mjs']);
});

test('empty user config still installs the network MCP', async () => {
  const { mergeNetworkMcp } = await import('../../network/configure.mjs');
  const merged = mergeNetworkMcp({});
  assert.ok(merged.mcpServers.agenthub_network);
});

test('invalid mcpServers shape is rejected', async () => {
  const { mergeNetworkMcp } = await import('../../network/configure.mjs');
  assert.throws(() => mergeNetworkMcp({ mcpServers: [] }), /invalid_mcp_config/);
  assert.throws(() => mergeNetworkMcp({ mcpServers: null }), /invalid_mcp_config/);
});

test('runtime wiring owns the network server for Claude, Codex, and Cursor', () => {
  const runtime = path.join(__dirname, '..', '..');
  const common = fs.readFileSync(path.join(runtime, 'common', 'entrypoint-common.sh'), 'utf8');
  const codex = fs.readFileSync(path.join(runtime, 'codex', 'entrypoint.sh'), 'utf8');
  const cursor = fs.readFileSync(path.join(runtime, 'cursor', 'entrypoint.sh'), 'utf8');

  assert.match(common, /AGENTHUB_NETWORK_MCP_ENABLED/);
  assert.match(common, /network\/configure\.mjs/);
  // Codex: user config may not spoof the reserved name; builtin block appended after.
  assert.match(codex, /agenthub_network/);
  assert.ok(codex.indexOf('agenthub_network') < codex.indexOf('--builtin'));
  assert.match(cursor, /AGENTHUB_NETWORK_MCP_ENABLED/);
  assert.match(cursor, /network\/configure\.mjs/);

  for (const provider of ['claude', 'codex', 'cursor', 'openclaw']) {
    const docker = fs.readFileSync(path.join(runtime, provider, 'Dockerfile'), 'utf8');
    assert.match(docker, /COPY network \/opt\/session-agent\/network/,
      `${provider} image ships the network MCP`);
  }
});

test('builtinToml renders agenthub_network with the callback env vars', () => {
  const { builtinToml } = require('../../codex/mcp-config');

  assert.doesNotMatch(builtinToml({}, '/usr/local/bin/node'), /agenthub_network/);
  const rendered = builtinToml({ AGENTHUB_NETWORK_MCP_ENABLED: '1' }, '/usr/local/bin/node');
  assert.match(rendered, /\[mcp_servers\.agenthub_network\]/);
  assert.match(rendered, /command = "\/usr\/local\/bin\/node"/);
  assert.match(rendered, /args = \["\/opt\/session-agent\/network\/server\.mjs"\]/);
  assert.match(rendered,
    /env_vars = \["PATH", "RUNTIME", "AGENTHUB_CALLBACK_URL", "AGENTHUB_CALLBACK_TOKEN"\]/);
});

test('Codex and Cursor conversion omit the runtime-reserved network server', () => {
  const codexConvert = require('../../codex/mcp-config').convertMcp;
  const codexToml = codexConvert({ mcpServers: {
    agenthub_network: { command: 'attacker' },
    docs: { command: 'docs' }
  } }, ['agenthub_network']);
  assert.doesNotMatch(codexToml, /attacker|agenthub_network/);
  assert.match(codexToml, /mcp_servers\.docs/);

  const cursorConvert = require('../../cursor/mcp-config').convertMcp;
  const cursorJson = JSON.parse(cursorConvert({ mcpServers: {
    agenthub_network: { command: 'attacker' },
    docs: { command: 'docs' }
  } }, ['agenthub_network']));
  assert.equal(cursorJson.mcpServers.agenthub_network, undefined);
  assert.equal(cursorJson.mcpServers.docs.command, 'docs');
});

test('entrypoint injects the network MCP only when AGENTHUB_NETWORK_MCP_ENABLED=1', () => {
  const runtime = path.join(__dirname, '..', '..');
  const common = fs.readFileSync(path.join(runtime, 'common', 'entrypoint-common.sh'), 'utf8');

  assert.match(common, /AGENTHUB_NETWORK_MCP_ENABLED:-0.*= "1"/);
});
