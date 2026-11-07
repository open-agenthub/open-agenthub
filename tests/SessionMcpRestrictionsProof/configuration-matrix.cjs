'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { pathToFileURL } = require('node:url');

// Run with an isolated output directory and an optional baseline repository. The
// preamble is taken from the actual entrypoint, so its forced Files default is covered.
async function main() {
  const [repository, output, configUrl] = process.argv.slice(2);
  assert(repository && output && configUrl, 'repository, output directory and config URL required');
  fs.mkdirSync(output, { recursive: true });
  const runtime = path.join(repository, 'agent-runtime');
  const source = fs.readFileSync(path.join(runtime, 'common/entrypoint-common.sh'), 'utf8');
  const preamble = source.slice(0, source.indexOf(': "${AGENTHUB_STATE_DIR:'))
    .replaceAll('/opt/session-agent', runtime).replaceAll('/opt/agenthub/session-agent', runtime);
  const builtinNames = ['agenthub_files', 'agenthub_browser', 'agenthub_sessions', 'agenthub_network', 'skill-library'];
  const environment = { PATH: process.env.PATH,
    AGENTHUB_BLOCKED_MCP_SERVERS: JSON.stringify(builtinNames),
    AGENTHUB_BROWSER_ENABLED: '1', AGENTHUB_SPAWN_MCP_ENABLED: '1',
    AGENTHUB_NETWORK_MCP_ENABLED: '1', AGENTHUB_SKILLS_MCP_ENABLED: '1' };
  const flags = JSON.parse(execFileSync('bash', ['-c', preamble + '\nnode -e \'console.log(JSON.stringify(process.env))\''],
    { env: environment, encoding: 'utf8' }));
  const response = await fetch(configUrl);
  assert(response.ok);
  const config = await response.json();
  const names = Object.keys(config.mcpServers || {});
  fs.writeFileSync(path.join(output, 'mcp.json'), JSON.stringify(config));
  const codex = require(path.join(runtime, 'codex/mcp-config.js'));
  const codexHome = path.join(output, 'codex'); fs.mkdirSync(codexHome, { recursive: true });
  fs.writeFileSync(path.join(codexHome, 'config.toml'), codex.convertMcp(config, [], flags) + codex.builtinToml(flags));
  const cursor = path.join(output, 'cursor'); fs.mkdirSync(path.join(cursor, '.cursor'), { recursive: true });
  fs.writeFileSync(path.join(cursor, '.cursor/mcp.json'), require(path.join(runtime, 'cursor/mcp-config.js')).convertMcp(JSON.stringify(config)));
  const openclaw = path.join(output, 'openclaw'); fs.mkdirSync(openclaw, { recursive: true });
  require(path.join(runtime, 'openclaw/mcp-config.js')).writeConfig(path.join(openclaw, 'openclaw.json'), path.join(output, 'mcp.json'), [], flags);
  const opencode = path.join(output, 'opencode'); fs.mkdirSync(opencode, { recursive: true });
  require(path.join(runtime, 'opencode/user-config.js')).writeUserConfig(path.join(opencode, 'opencode.json'),
    { ...flags, AGENTHUB_MCP_CONFIG: path.join(output, 'mcp.json') });
  const claude = path.join(output, 'claude'); fs.mkdirSync(claude, { recursive: true });
  const claudeConfig = await import(pathToFileURL(path.join(runtime, 'claude/mcp-config.mjs')));
  claudeConfig.writeMcpConfig(path.join(claude, '.claude.json'), path.join(output, 'mcp.json'));
  const summary = { names, flags: Object.fromEntries(Object.entries(flags).filter(([name]) =>
    name.endsWith('_MCP_ENABLED') || name === 'AGENTHUB_BROWSER_ENABLED')) };
  if (fs.existsSync(path.join(runtime, 'common/mcp-startup-policy.js'))) {
    for (const value of Object.values(summary.flags)) assert.equal(value, '0');
    assert(!names.includes('probe-srv'));
  }
  fs.writeFileSync(path.join(output, 'summary.json'), JSON.stringify(summary, null, 2));
  console.log(JSON.stringify(summary));
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
