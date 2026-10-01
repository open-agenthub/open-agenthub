'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');

function tempFiles(configContents, mcpContents) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-claude-mcp-'));
  const configPath = path.join(dir, '.claude.json');
  const mcpPath = path.join(dir, 'agenthub-mcp.json');
  if (configContents !== undefined) fs.writeFileSync(configPath, configContents);
  if (mcpContents !== undefined) fs.writeFileSync(mcpPath, mcpContents);
  return { configPath, mcpPath };
}

const SERVERS = JSON.stringify({
  mcpServers: { agenthub_files: { command: 'node', args: ['files/server.mjs'] } }
});

test('session MCP servers land in the user scope, not in the workspace', async () => {
  const { writeMcpConfig } = await import('../../claude/mcp-config.mjs');
  const { configPath, mcpPath } = tempFiles(
    JSON.stringify({ hasCompletedOnboarding: true }), SERVERS);

  writeMcpConfig(configPath, mcpPath);
  const written = JSON.parse(fs.readFileSync(configPath, 'utf8'));

  // Verified against Claude Code 2.1.283: a user-scoped server is connected straight away, while
  // one from a project .mcp.json reports "Pending approval" and is never connected to.
  assert.deepEqual(written.mcpServers.agenthub_files,
    { command: 'node', args: ['files/server.mjs'] });
  assert.equal(written.hasCompletedOnboarding, true);
});

test('the trust settings written just before are preserved', async () => {
  const { writeMcpConfig } = await import('../../claude/mcp-config.mjs');
  // workspace-trust.mjs writes the same file first; a wholesale overwrite would drop its work and
  // leave the session sitting on the safety dialog again.
  const { configPath, mcpPath } = tempFiles(JSON.stringify({
    hasCompletedOnboarding: true,
    theme: 'dark',
    projects: { '/workspace/repo': { hasTrustDialogAccepted: true } }
  }), SERVERS);

  writeMcpConfig(configPath, mcpPath);
  const written = JSON.parse(fs.readFileSync(configPath, 'utf8'));

  assert.equal(written.projects['/workspace/repo'].hasTrustDialogAccepted, true);
  assert.equal(written.theme, 'dark');
  assert.ok(written.mcpServers.agenthub_files);
});

test('a server dropped from the session config disappears on the next start', async () => {
  const { mergeMcpServers, MANAGED_KEY } = await import('../../claude/mcp-config.mjs');
  // ~/.claude.json is restored from the session's own state tar, so it comes back holding whatever
  // the previous incarnation wrote.
  const previous = mergeMcpServers({}, {
    agenthub_files: { command: 'node' }, agenthub_browser: { command: 'node' }
  });
  assert.deepEqual(previous[MANAGED_KEY], ['agenthub_files', 'agenthub_browser']);

  const next = mergeMcpServers(previous, { agenthub_files: { command: 'node' } });

  assert.deepEqual(Object.keys(next.mcpServers), ['agenthub_files']);
  assert.deepEqual(next[MANAGED_KEY], ['agenthub_files']);
});

test('a server a custom image added itself is never withdrawn', async () => {
  const { mergeMcpServers } = await import('../../claude/mcp-config.mjs');
  // Which is why the managed names are tracked rather than mcpServers being replaced wholesale.
  const config = {
    mcpServers: { image_own: { command: 'node' } }
  };

  const merged = mergeMcpServers(config, { agenthub_files: { command: 'node' } });
  assert.ok(merged.mcpServers.image_own);

  const afterRemoval = mergeMcpServers(merged, {});
  assert.deepEqual(Object.keys(afterRemoval.mcpServers), ['image_own']);
});

test('a session without MCP leaves no managed servers and no bookkeeping behind', async () => {
  const { writeMcpConfig, MANAGED_KEY } = await import('../../claude/mcp-config.mjs');
  const { configPath } = tempFiles(JSON.stringify({ hasCompletedOnboarding: true }));

  // The entrypoint passes an empty string when AGENTHUB_MCP_CONFIG is unset.
  writeMcpConfig(configPath, '');
  const written = JSON.parse(fs.readFileSync(configPath, 'utf8'));

  assert.equal(written.mcpServers, undefined);
  assert.equal(written[MANAGED_KEY], undefined);
  assert.equal(written.hasCompletedOnboarding, true);
});

test('a missing or corrupt config file does not stop the session', async () => {
  const { writeMcpConfig } = await import('../../claude/mcp-config.mjs');

  for (const contents of [undefined, 'not json at all', '[]']) {
    const { configPath, mcpPath } = tempFiles(contents, SERVERS);
    writeMcpConfig(configPath, mcpPath);
    assert.ok(JSON.parse(fs.readFileSync(configPath, 'utf8')).mcpServers.agenthub_files);
  }
});

test('a corrupt MCP config contributes no servers rather than throwing', async () => {
  const { writeMcpConfig } = await import('../../claude/mcp-config.mjs');
  const { configPath, mcpPath } = tempFiles('{}', 'not json');

  writeMcpConfig(configPath, mcpPath);
  assert.equal(JSON.parse(fs.readFileSync(configPath, 'utf8')).mcpServers, undefined);
});

test('no runtime copies the MCP config into the workspace any more', () => {
  const common = fs.readFileSync(
    path.join(runtimeDir, 'common', 'entrypoint-common.sh'), 'utf8');

  // With a single repository the workdir is the clone, so this left an untracked file in a tree
  // the agent is about to commit — and the server it declared was never connected to anyway.
  assert.doesNotMatch(common, /cp .*\$TARGET\/\.mcp\.json/);
  assert.doesNotMatch(common, /\$TARGET\/\.mcp\.json/);
  // The config itself still has to be resolved; only its destination changed.
  assert.match(common, /export AGENTHUB_MCP_CONFIG/);

  const claude = fs.readFileSync(path.join(runtimeDir, 'claude', 'entrypoint.sh'), 'utf8');
  assert.match(claude,
    /node "\$RUNTIME\/claude\/mcp-config\.mjs" "\$HOME\/\.claude\.json" "\$\{AGENTHUB_MCP_CONFIG:-\}"/);
});

test('codex and cursor keep their own central configs and need no workspace file', () => {
  // Confirmed with `codex mcp list` and `cursor-agent mcp list`: neither reads .mcp.json, and
  // cursor names its own locations in the error it prints when none is configured.
  const codex = fs.readFileSync(path.join(runtimeDir, 'codex', 'entrypoint.sh'), 'utf8');
  assert.match(codex, /codex\/mcp-config\.js" --builtin >> "\$CODEX_HOME\/config\.toml"/);

  const cursor = fs.readFileSync(path.join(runtimeDir, 'cursor', 'entrypoint.sh'), 'utf8');
  assert.match(cursor, /"\$CURSOR_CONFIG_DIR\/mcp\.json"/);
});
