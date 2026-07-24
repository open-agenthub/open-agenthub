'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { convertMcp } = require('../../codex/mcp-config');

test('Codex MCP conversion renders sorted deterministic stdio tables and JSON escaping', () => {
  const config = { mcpServers: {
    zebra: { command: 'node', args: ['line\nbreak', 'quote"\\tail'], env: { ZED: 'z', ALPHA: 'a\n"' } },
    alpha: { command: 'npx', args: ['-y', 'server'] }
  } };
  const first = convertMcp(config);
  assert.equal(first, convertMcp(JSON.stringify(config)));
  assert.ok(first.indexOf('[mcp_servers.alpha]') < first.indexOf('[mcp_servers.zebra]'));
  assert.match(first, /command = "npx"/);
  assert.match(first, /args = \["line\\nbreak", "quote\\"\\\\tail"\]/);
  assert.match(first, /env = \{ "ALPHA" = "a\\n\\\"", "ZED" = "z" \}/);
  assert.doesNotMatch(first, /\n\[mcp_servers\.injected\]/);
});

test('Codex MCP conversion supports safe HTTP auth headers and tool filters', () => {
  const toml = convertMcp({ mcpServers: { docs: {
    type: 'http', url: 'https://mcp.example.test/mcp', bearerTokenEnvVar: 'DOCS_TOKEN',
    headers: { 'X-Region': 'eu-central', 'X-Trace': '${TRACE_ID}' }, enabled: false,
    enabledTools: ['search', 'read'], disabledTools: ['delete']
  } } });
  assert.match(toml, /url = "https:\/\/mcp\.example\.test\/mcp"/);
  assert.match(toml, /bearer_token_env_var = "DOCS_TOKEN"/);
  assert.match(toml, /http_headers = \{ "X-Region" = "eu-central" \}/);
  assert.match(toml, /env_http_headers = \{ "X-Trace" = "TRACE_ID" \}/);
  assert.match(toml, /enabled = false/);
  assert.match(toml, /enabled_tools = \["search", "read"\]/);
  assert.match(toml, /disabled_tools = \["delete"\]/);
});

test('Codex MCP conversion maps environment Authorization without storing its value', () => {
  const toml = convertMcp({ mcpServers: { remote: {
    type: 'http', url: 'https://example.test/mcp', headers: { Authorization: 'Bearer ${REMOTE_TOKEN}' }
  } } });
  assert.match(toml, /bearer_token_env_var = "REMOTE_TOKEN"/);
  assert.doesNotMatch(toml, /Authorization|Bearer/);
});

test('Codex MCP conversion rejects unsupported ambiguous and secret-bearing input', () => {
  assert.throws(() => convertMcp({ mcpServers: { old: { type: 'sse', url: 'https://x.test' } } }), /unsupported transport/i);
  assert.throws(() => convertMcp({ mcpServers: { mixed: { command: 'npx', url: 'https://x.test' } } }), /ambiguous|both command and url/i);
  assert.throws(() => convertMcp({ mcpServers: { secret: {
    type: 'http', url: 'https://x.test', headers: { Authorization: 'Bearer literal-secret' }
  } } }), /literal authorization|secret/i);
  assert.throws(() => convertMcp({ mcpServers: { secret: {
    type: 'http', url: 'https://user:password@x.test/mcp'
  } } }), /credentials|unsafe/i);
  assert.throws(() => convertMcp({ mcpServers: { 'bad\n[mcp_servers.injected]': { command: 'x' } } }), /invalid server name/i);
  assert.throws(() => convertMcp({ mcpServers: { duplicate: {
    type: 'http', url: 'https://x.test', envHeaders: { Authorization: 'FIRST_TOKEN' },
    headers: { authorization: 'Bearer ${SECOND_TOKEN}' }
  } } }), /duplicate|ambiguous/i);
  assert.throws(() => convertMcp({ mcpServers: { duplicate: {
    type: 'http', url: 'https://x.test', bearerTokenEnvVar: 'FIRST_TOKEN',
    envHeaders: { AUTHORIZATION: 'SECOND_TOKEN' }
  } } }), /duplicate|ambiguous/i);
});

test('Codex MCP conversion rejects type confusion and user security configuration', () => {
  assert.throws(() => convertMcp({ mcpServers: [] }), /mcpServers.*object/i);
  assert.throws(() => convertMcp({ mcpServers: { bad: { command: ['not-a-string'] } } }), /command.*string/i);
  assert.throws(() => convertMcp({ mcpServers: { bad: { command: 'x', args: 'not-an-array' } } }), /args.*array/i);
  assert.throws(() => convertMcp({ mcpServers: {}, cli_auth_credentials_store: 'keyring' }), /unsupported top-level|security/i);
  assert.throws(() => convertMcp({ mcpServers: {}, mcp_oauth_callback_url: 'https://evil.test/callback' }), /unsupported top-level|security/i);
});
