'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { convertMcp } = require('../../cursor/mcp-config');
test('Cursor conversion omits the runtime-reserved files server', () => {
  const output = JSON.parse(convertMcp({ mcpServers: {
    agenthub_files: { command: 'attacker' },
    custom: { command: 'safe' }
  } }, ['agenthub_files']));
  assert.equal(output.mcpServers.agenthub_files, undefined);
  assert.equal(output.mcpServers.custom.command, 'safe');
});


test('Cursor MCP conversion renders sorted deterministic stdio servers as mcp.json', () => {
  const config = { mcpServers: {
    zebra: { command: 'node', args: ['line\nbreak', 'quote"\\tail'], env: { ZED: 'z', ALPHA: 'a\n"' } },
    alpha: { command: 'npx', args: ['-y', 'server'] }
  } };
  const first = convertMcp(config);
  assert.equal(first, convertMcp(JSON.stringify(config)));
  const parsed = JSON.parse(first);
  assert.deepEqual(Object.keys(parsed.mcpServers), ['alpha', 'zebra']);
  assert.equal(parsed.mcpServers.alpha.command, 'npx');
  assert.deepEqual(parsed.mcpServers.alpha.args, ['-y', 'server']);
  assert.equal(parsed.mcpServers.zebra.env.ALPHA, 'a\n"');
});

test('Cursor MCP conversion supports safe HTTP servers and tool filters', () => {
  const json = convertMcp({ mcpServers: { docs: {
    type: 'http', url: 'https://mcp.example.test/mcp',
    headers: { Authorization: 'Bearer ${DOCS_TOKEN}', 'X-Region': 'eu-central' },
    env: undefined, enabled: false
  } } });
  const parsed = JSON.parse(json);
  assert.equal(parsed.mcpServers.docs.url, 'https://mcp.example.test/mcp');
  assert.equal(parsed.mcpServers.docs.headers.Authorization, 'Bearer ${DOCS_TOKEN}');
  assert.equal(parsed.mcpServers.docs.headers['X-Region'], 'eu-central');
  assert.equal(parsed.mcpServers.docs.enabled, false);
});

test('Cursor MCP conversion maps streamable-http to http', () => {
  const parsed = JSON.parse(convertMcp({ mcpServers: { remote: {
    type: 'streamable-http', url: 'https://example.test/mcp'
  } } }));
  assert.equal(parsed.mcpServers.remote.type, 'http');
  assert.equal(parsed.mcpServers.remote.url, 'https://example.test/mcp');
});

test('Cursor MCP conversion rejects unsupported ambiguous and secret-bearing input', () => {
  assert.throws(() => convertMcp({ mcpServers: { old: { type: 'sse', url: 'https://x.test' } } }),
    /unsupported transport/i);
  assert.throws(() => convertMcp({ mcpServers: { mixed: { command: 'npx', url: 'https://x.test' } } }),
    /ambiguous|both command and url/i);
  assert.throws(() => convertMcp({ mcpServers: { secret: {
    type: 'http', url: 'https://x.test', headers: { Authorization: 'Bearer literal-secret' }
  } } }), /literal authorization|secret/i);
  assert.throws(() => convertMcp({ mcpServers: { secret: {
    type: 'http', url: 'https://user:password@x.test/mcp'
  } } }), /credentials|unsafe/i);
  assert.throws(() => convertMcp({ mcpServers: { 'bad name!': { command: 'x' } } }),
    /invalid server name/i);
});

test('Cursor MCP conversion rejects type confusion and user security configuration', () => {
  assert.throws(() => convertMcp({ mcpServers: [] }), /mcpServers.*object/i);
  assert.throws(() => convertMcp({ mcpServers: { bad: { command: ['not-a-string'] } } }),
    /command.*string/i);
  assert.throws(() => convertMcp({ mcpServers: { bad: { command: 'x', args: 'not-an-array' } } }),
    /args.*array/i);
  assert.throws(() => convertMcp({ mcpServers: {}, approvalMode: 'unrestricted' }),
    /unsupported top-level|security/i);
});
