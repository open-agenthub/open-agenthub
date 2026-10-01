'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('built-in sessions MCP cannot be replaced by user config', async () => {
  const { mergeSessionsMcp } = await import('../../sessions/configure.mjs');
  const merged = mergeSessionsMcp({ mcpServers: {
    agenthub_sessions: { command: 'attacker' },
    docs: { command: 'docs' }
  }});

  assert.equal(merged.mcpServers.agenthub_sessions.command, 'node');
  assert.deepEqual(merged.mcpServers.agenthub_sessions.args,
    ['/opt/session-agent/sessions/server.mjs']);
  assert.equal(merged.mcpServers.docs.command, 'docs');
});

test('custom-image runtime path is used for the sessions MCP', async () => {
  const { mergeSessionsMcp } = await import('../../sessions/configure.mjs');
  const merged = mergeSessionsMcp({}, '/opt/agenthub/session-agent');
  assert.deepEqual(merged.mcpServers.agenthub_sessions.args,
    ['/opt/agenthub/session-agent/sessions/server.mjs']);
});

test('empty user config still installs sessions MCP', async () => {
  const { mergeSessionsMcp } = await import('../../sessions/configure.mjs');
  const merged = mergeSessionsMcp({});
  assert.ok(merged.mcpServers.agenthub_sessions);
});

test('runtime wiring owns the sessions server for Claude, Codex, and Cursor', () => {
  const runtime = path.join(__dirname, '..', '..');
  const common = fs.readFileSync(path.join(runtime, 'common', 'entrypoint-common.sh'), 'utf8');
  const codex = fs.readFileSync(path.join(runtime, 'codex', 'entrypoint.sh'), 'utf8');
  const cursor = fs.readFileSync(path.join(runtime, 'cursor', 'entrypoint.sh'), 'utf8');
  const claudeDocker = fs.readFileSync(path.join(runtime, 'claude', 'Dockerfile'), 'utf8');
  const codexDocker = fs.readFileSync(path.join(runtime, 'codex', 'Dockerfile'), 'utf8');
  const cursorDocker = fs.readFileSync(path.join(runtime, 'cursor', 'Dockerfile'), 'utf8');
  const server = fs.readFileSync(path.join(runtime, 'sessions', 'server.mjs'), 'utf8');

  assert.match(common, /AGENTHUB_SPAWN_MCP_ENABLED/);
  // Guarded on module presence, so a runtime whose image lacks it skips instead of aborting.
  assert.match(common, /merge_builtin_mcp sessions configure\.mjs/);
  assert.ok(codex.indexOf('/secrets/mcp/mcp.json') < codex.indexOf('--builtin'));
  assert.match(codex, /agenthub_sessions >> "\$CODEX_HOME\/config\.toml"|agenthub_sessions/);
  assert.match(require('../../codex/mcp-config').builtinToml(
    { AGENTHUB_SPAWN_MCP_ENABLED: '1' }, '/usr/local/bin/node'),
    /\[mcp_servers\.agenthub_sessions\]/);
  assert.match(cursor, /AGENTHUB_SPAWN_MCP_ENABLED/);
  assert.match(cursor, /agenthub_sessions|sessions\/configure\.mjs/);
  assert.match(claudeDocker, /COPY sessions \/opt\/session-agent\/sessions/);
  assert.match(codexDocker, /COPY sessions \/opt\/session-agent\/sessions/);
  assert.match(cursorDocker, /COPY sessions \/opt\/session-agent\/sessions/);
  for (const name of [
    'session_create', 'session_get', 'session_list', 'session_wait', 'session_delete'
  ]) assert.match(server, new RegExp(`register\\('${name}'`));
});

test('Codex conversion omits runtime-reserved sessions server', () => {
  const { convertMcp } = require('../../codex/mcp-config');
  const converted = convertMcp({ mcpServers: {
    agenthub_sessions: { command: 'attacker' },
    docs: { command: 'docs' }
  } }, ['agenthub_sessions']);

  assert.doesNotMatch(converted, /attacker|agenthub_sessions/);
  assert.match(converted, /mcp_servers\.docs/);
});

test('Cursor conversion omits runtime-reserved sessions server', () => {
  const { convertMcp } = require('../../cursor/mcp-config');
  const converted = convertMcp({ mcpServers: {
    agenthub_sessions: { command: 'attacker' },
    docs: { command: 'docs' }
  } }, ['agenthub_sessions']);

  assert.doesNotMatch(converted, /attacker|agenthub_sessions/);
  const parsed = JSON.parse(converted);
  assert.equal(parsed.mcpServers.docs.command, 'docs');
  assert.equal(parsed.mcpServers.agenthub_sessions, undefined);
});

test('entrypoint injects sessions only when AGENTHUB_SPAWN_MCP_ENABLED=1', () => {
  const runtime = path.join(__dirname, '..', '..');
  const common = fs.readFileSync(path.join(runtime, 'common', 'entrypoint-common.sh'), 'utf8');
  const { builtinToml } = require('../../codex/mcp-config');

  assert.match(common, /AGENTHUB_SPAWN_MCP_ENABLED:-0.*= "1"/);
  assert.doesNotMatch(builtinToml({}, '/usr/local/bin/node'), /agenthub_sessions/);
  assert.match(builtinToml({ AGENTHUB_SPAWN_MCP_ENABLED: '1' }, '/usr/local/bin/node'),
    /agenthub_sessions/);
});
