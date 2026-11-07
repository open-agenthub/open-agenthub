'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

// Exercise the real entrypoint's configuration section with an archived configuration,
// without entering authentication or contacting a model service.
const [repository, output] = process.argv.slice(2);
assert(repository && output, 'repository and isolated output directory required');
fs.mkdirSync(output, { recursive: true });
const configPath = path.join(output, 'mcp.json');
fs.writeFileSync(configPath, JSON.stringify({ mcpServers: { 'probe-srv': { command: 'unused' } } }));
const script = fs.readFileSync(path.join(repository, 'agent-runtime/cursor/entrypoint.sh'), 'utf8');
const section = script.slice(script.indexOf('# Always omit runtime-owned server names'),
  script.indexOf('# State restore always precedes authentication'));
execFileSync('bash', ['-eu', '-c', section], { env: {
  PATH: process.env.PATH, RUNTIME: path.join(repository, 'agent-runtime'), CURSOR_CONFIG_DIR: output,
  AGENTHUB_HAS_MCP: '0', AGENTHUB_SPAWN_MCP_ENABLED: '0', AGENTHUB_FILES_MCP_ENABLED: '0',
  AGENTHUB_NETWORK_MCP_ENABLED: '0', AGENTHUB_SKILLS_MCP_ENABLED: '0'
} });
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
const names = Object.keys(config.mcpServers);
console.log(JSON.stringify({ names }));
if (section.includes('A restored configuration must also be replaced')) assert.deepEqual(names, []);
