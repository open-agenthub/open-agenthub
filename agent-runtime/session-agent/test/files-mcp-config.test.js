const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const runtimeRoot = path.join(__dirname, '..', '..');

test('managed files MCP replaces a spoofed user definition', async () => {
  const { mergeFilesMcp } = await import('../../files/configure.mjs');
  const merged = mergeFilesMcp({ mcpServers: {
    agenthub_files: { command: 'attacker' },
    custom: { command: 'custom' }
  } }, '/opt/agenthub/session-agent');

  assert.deepEqual(merged.mcpServers.agenthub_files, {
    command: 'node', args: ['/opt/agenthub/session-agent/files/server.mjs']
  });
  assert.equal(merged.mcpServers.custom.command, 'custom');
});

test('all runtime entrypoints own and enable agenthub_files', () => {
  const common = fs.readFileSync(path.join(runtimeRoot, 'common', 'entrypoint-common.sh'), 'utf8');
  const codex = fs.readFileSync(path.join(runtimeRoot, 'codex', 'entrypoint.sh'), 'utf8');
  const cursor = fs.readFileSync(path.join(runtimeRoot, 'cursor', 'entrypoint.sh'), 'utf8');
  assert.match(common, /AGENTHUB_FILES_MCP_ENABLED/);
  assert.match(common, /files\/configure\.mjs/);
  assert.match(codex, /agenthub_files/);
  assert.match(common, /cp "\$AGENTHUB_MCP_CONFIG" "\$TARGET\/\.mcp\.json"/);
  assert.match(cursor, /agenthub_files/);
  for (const provider of ['claude', 'codex', 'cursor', 'openclaw']) {
    const dockerfile = fs.readFileSync(path.join(runtimeRoot, provider, 'Dockerfile'), 'utf8');
    assert.match(dockerfile, /COPY files \/opt\/session-agent\/files/);
  }
});
