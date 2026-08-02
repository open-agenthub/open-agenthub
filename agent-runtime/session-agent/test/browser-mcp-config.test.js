'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('built-in MCP cannot be replaced by user config', async () => {
  const { mergeClaudeMcp } = await import('../../browser/configure-claude.mjs');
  const merged = mergeClaudeMcp({ mcpServers: {
    agenthub_browser: { command: 'attacker' },
    docs: { command: 'docs' }
  }});

  assert.equal(merged.mcpServers.agenthub_browser.command, 'node');
  assert.deepEqual(merged.mcpServers.agenthub_browser.args,
    ['/opt/session-agent/browser/server.mjs']);
  assert.equal(merged.mcpServers.docs.command, 'docs');
});

test('custom-image runtime path is used for the built-in MCP', async () => {
  const { mergeClaudeMcp } = await import('../../browser/configure-claude.mjs');
  const merged = mergeClaudeMcp({}, '/opt/agenthub/session-agent');
  assert.deepEqual(merged.mcpServers.agenthub_browser.args,
    ['/opt/agenthub/session-agent/browser/server.mjs']);
});
test('empty user config still installs browser MCP', async () => {
  const { mergeClaudeMcp } = await import('../../browser/configure-claude.mjs');
  const merged = mergeClaudeMcp({});
  assert.ok(merged.mcpServers.agenthub_browser);
});
test('runtime wiring owns the browser server for both Claude and Codex', () => {
  const runtime = path.join(__dirname, '..', '..');
  const common = fs.readFileSync(path.join(runtime, 'common', 'entrypoint-common.sh'), 'utf8');
  const codex = fs.readFileSync(path.join(runtime, 'codex', 'entrypoint.sh'), 'utf8');
  const claudeDocker = fs.readFileSync(path.join(runtime, 'claude', 'Dockerfile'), 'utf8');
  const codexDocker = fs.readFileSync(path.join(runtime, 'codex', 'Dockerfile'), 'utf8');
  const server = fs.readFileSync(path.join(runtime, 'browser', 'server.mjs'), 'utf8');

  assert.match(common, /AGENTHUB_MCP_CONFIG=\/tmp\/agenthub-mcp\.json/);
  assert.ok(codex.indexOf('mcp-config.js') < codex.indexOf('[mcp_servers.agenthub_browser]'));
  assert.match(codex, /agenthub_browser(?:\s+agenthub_sessions)?(?:\s+agenthub_files)? >> "\$CODEX_HOME\/config\.toml"/);
  assert.match(claudeDocker, /COPY browser \/opt\/session-agent\/browser/);
  assert.match(codexDocker, /COPY browser \/opt\/session-agent\/browser/);
  for (const name of [
    'browser_start', 'browser_status', 'browser_stop', 'browser_navigate',
    'browser_snapshot', 'browser_click', 'browser_type', 'browser_screenshot', 'browser_tabs'
  ]) assert.match(server, new RegExp(`register\\('${name}'`));
});

test('Codex conversion omits runtime-reserved browser server', () => {
  const { convertMcp } = require('../../codex/mcp-config');
  const converted = convertMcp({ mcpServers: {
    agenthub_browser: { command: 'attacker' },
    docs: { command: 'docs' }
  } }, ['agenthub_browser']);

  assert.doesNotMatch(converted, /attacker|agenthub_browser/);
  assert.match(converted, /mcp_servers\.docs/);
});
test('Claude driver uses the runtime-owned merged MCP path', () => {
  const driver = require('../../claude/driver');
  const command = driver.buildCommand({
    AGENTHUB_MODE: 'interactive',
    AGENTHUB_HAS_MCP: '1',
    AGENTHUB_MCP_CONFIG: '/tmp/agenthub-mcp.json',
    AGENTHUB_RESUME: '0',
    AGENTHUB_CLAUDE_SESSION_ID: ''
  }, true);
  assert.deepEqual(command.args, ['--mcp-config', '/tmp/agenthub-mcp.json']);
});