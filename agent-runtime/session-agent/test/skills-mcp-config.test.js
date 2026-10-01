const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const runtimeRoot = path.join(__dirname, '..', '..');
const hubEntry = {
  type: 'http',
  url: 'https://hub.example.com/internal/sessions/s1/mcp',
  headers: { 'X-Agent-Token': 'secret' }
};

test('the hub skill-library entry is replaced by the local proxy', async () => {
  const { mergeSkillsMcp } = await import('../../skills/configure.mjs');
  const merged = mergeSkillsMcp(
    { mcpServers: { 'skill-library': hubEntry, custom: { command: 'custom' } } },
    '/opt/agenthub/session-agent', {});

  assert.deepEqual(merged.mcpServers['skill-library'], {
    command: 'node', args: ['/opt/agenthub/session-agent/skills/server.mjs']
  });
  assert.equal(merged.mcpServers.custom.command, 'custom');
});

test('without an injected entry nothing is added', async () => {
  const { mergeSkillsMcp } = await import('../../skills/configure.mjs');
  // The hub injects the entry exactly when the feature is on for the session, so its
  // absence is the signal not to run a proxy in front of nothing.
  const merged = mergeSkillsMcp({ mcpServers: { custom: { command: 'custom' } } }, '/opt', {});

  assert.equal(merged.mcpServers['skill-library'], undefined);
});

test("a user's own stdio skill-library definition is left alone", async () => {
  const { mergeSkillsMcp } = await import('../../skills/configure.mjs');
  const merged = mergeSkillsMcp(
    { mcpServers: { 'skill-library': { command: 'their-own-server' } } }, '/opt', {});

  assert.deepEqual(merged.mcpServers['skill-library'], { command: 'their-own-server' });
});

test('the proxy can be switched off and the plain HTTP entry stays', async () => {
  const { mergeSkillsMcp } = await import('../../skills/configure.mjs');
  const merged = mergeSkillsMcp({ mcpServers: { 'skill-library': hubEntry } }, '/opt',
    { AGENTHUB_SKILLS_MCP_ENABLED: '0' });

  assert.deepEqual(merged.mcpServers['skill-library'], hubEntry);
});

test('codex renders the proxy as a stdio server with the callback variables forwarded', () => {
  const { convertMcp } = require('../../codex/mcp-config.js');
  const toml = convertMcp({ mcpServers: { 'skill-library': hubEntry } }, [],
    { AGENTHUB_SKILLS_MCP_ENABLED: '1', RUNTIME: '/opt/session-agent' }, '/usr/bin/node');

  assert.match(toml, /\[mcp_servers\.skill-library\]/);
  assert.match(toml, /command = "\/usr\/bin\/node"/);
  assert.match(toml, /args = \["\/opt\/session-agent\/skills\/server\.mjs"\]/);
  // Codex clears the environment for MCP subprocesses; without these the proxy cannot
  // reach the hub and dies before initialize.
  assert.match(toml, /AGENTHUB_CALLBACK_URL/);
  assert.match(toml, /AGENTHUB_CALLBACK_TOKEN/);
  assert.doesNotMatch(toml, /url = /);
});

test('codex keeps the HTTP entry when the proxy is off', () => {
  const { convertMcp } = require('../../codex/mcp-config.js');
  const toml = convertMcp({ mcpServers: { 'skill-library': hubEntry } }, [],
    { AGENTHUB_SKILLS_MCP_ENABLED: '0' }, '/usr/bin/node');

  assert.match(toml, /url = "https:\/\/hub\.example\.com/);
  assert.doesNotMatch(toml, /skills\/server\.mjs/);
});

test('every runtime ships the proxy and wires it up', () => {
  const common = fs.readFileSync(path.join(runtimeRoot, 'common', 'entrypoint-common.sh'), 'utf8');
  const cursor = fs.readFileSync(path.join(runtimeRoot, 'cursor', 'entrypoint.sh'), 'utf8');
  assert.match(common, /AGENTHUB_SKILLS_MCP_ENABLED/);
  assert.match(common, /skills\/configure\.mjs/);
  assert.match(cursor, /skills\/configure\.mjs/);
  for (const provider of ['claude', 'codex', 'cursor', 'openclaw']) {
    const dockerfile = fs.readFileSync(path.join(runtimeRoot, provider, 'Dockerfile'), 'utf8');
    assert.match(dockerfile, /COPY skills \/opt\/session-agent\/skills/);
  }
});
