'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const mcp = require('../../openclaw/mcp-config');

function runtimeWith(dirs) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-oc-rt-'));
  for (const dir of dirs) {
    fs.mkdirSync(path.join(root, dir), { recursive: true });
    fs.writeFileSync(path.join(root, dir, 'server.mjs'), '');
  }
  return root;
}

function builtinEnv(root, overrides = {}) {
  return {
    RUNTIME: root,
    AGENTHUB_FILES_MCP_ENABLED: '1',
    AGENTHUB_NETWORK_MCP_ENABLED: '1',
    AGENTHUB_CALLBACK_URL: 'http://callback',
    AGENTHUB_CALLBACK_TOKEN: 'tok',
    AGENTHUB_WORKDIR: '/workspace/repo',
    PATH: '/usr/bin',
    ...overrides
  };
}

test('a user MCP document becomes OpenClaw server entries', () => {
  const servers = mcp.convertMcp(JSON.stringify({
    mcpServers: {
      user_tool: { command: 'node', args: ['/srv.mjs'], env: { TOKEN: 'abc' } }
    }
  }));

  assert.deepEqual(servers.user_tool, {
    enabled: true, command: 'node', args: ['/srv.mjs'], env: { TOKEN: 'abc' }
  });
  // A command is what makes an entry stdio; a server configured with command/args alone is listed
  // and spawned, so stating a transport would add nothing.
  assert.equal(servers.user_tool.transport, undefined);
});

test('runtime-owned names cannot be shadowed by a user config', () => {
  const servers = mcp.convertMcp(JSON.stringify({
    mcpServers: { agenthub_files: { command: 'evil' }, mine: { command: 'node' } }
  }), ['agenthub_browser', 'agenthub_sessions', 'agenthub_files', 'agenthub_network']);

  assert.deepEqual(Object.keys(servers), ['mine']);
});

test('tool filters move to the shape OpenClaw expects', () => {
  const servers = mcp.convertMcp(JSON.stringify({
    mcpServers: {
      t: { command: 'node', enabledTools: ['a'], disabledTools: ['b'] }
    }
  }));

  // Flat enabled/disabled lists are an AgentHub shape; OpenClaw filters via toolFilter.
  assert.deepEqual(servers.t.toolFilter, { include: ['a'], exclude: ['b'] });
  assert.equal(servers.t.enabledTools, undefined);
});

test('an HTTP server keeps its transport and rejects credentials in the URL', () => {
  const servers = mcp.convertMcp(JSON.stringify({
    mcpServers: { remote: { url: 'https://mcp.example.com/mcp', headers: { 'X-A': 'b' } } }
  }));
  assert.equal(servers.remote.transport, 'streamable-http');
  assert.equal(servers.remote.url, 'https://mcp.example.com/mcp');

  assert.throws(() => mcp.convertMcp(JSON.stringify({
    mcpServers: { remote: { url: 'https://user:pw@mcp.example.com/mcp' } }
  })), /credentials are unsafe/);
});

test('a literal Authorization secret is refused, an env-backed one is kept', () => {
  assert.throws(() => mcp.convertMcp(JSON.stringify({
    mcpServers: { r: { url: 'https://x.example.com', headers: { Authorization: 'Bearer hunter2' } } }
  })), /Literal Authorization secrets/);

  const servers = mcp.convertMcp(JSON.stringify({
    mcpServers: { r: { url: 'https://x.example.com', bearerTokenEnvVar: 'TOKEN_VAR' } }
  }));
  assert.equal(servers.r.headers.Authorization, 'Bearer ${TOKEN_VAR}');
});

test('a malformed user document is rejected rather than silently dropped', () => {
  assert.throws(() => mcp.convertMcp('not json'), /must be valid JSON/);
  assert.throws(() => mcp.convertMcp(JSON.stringify({ mcpServers: {}, other: 1 })),
    /Unsupported top-level security configuration/);
  assert.throws(() => mcp.convertMcp(JSON.stringify({
    mcpServers: { s: { command: 'node', url: 'https://x.example.com' } }
  })), /ambiguous/);
});

test('builtin servers carry env references, never literal secrets', () => {
  const root = runtimeWith(['files', 'network']);
  const servers = mcp.builtinServers(builtinEnv(root), '/usr/local/bin/node');

  // OpenClaw clears the environment for an MCP child — a probe child saw two variables, neither
  // RUNTIME nor the callback token — so each variable has to be named here.
  assert.deepEqual(servers.agenthub_files.env, {
    PATH: '${PATH}', RUNTIME: '${RUNTIME}',
    AGENTHUB_CALLBACK_URL: '${AGENTHUB_CALLBACK_URL}',
    AGENTHUB_CALLBACK_TOKEN: '${AGENTHUB_CALLBACK_TOKEN}',
    AGENTHUB_WORKDIR: '${AGENTHUB_WORKDIR}'
  });
  // References, not values: ~/.openclaw is archived into the session's state tar and uploaded, so
  // a literal token here would be a credential leaving the pod.
  assert.equal(JSON.stringify(servers).includes('tok'), false);
  // AGENTHUB_FILE_ROOT is unset in this environment; emitting it would make OpenClaw warn
  // "Missing env var" on every command.
  assert.equal(servers.agenthub_files.env.AGENTHUB_FILE_ROOT, undefined);
  // Only the files server needs the workdir.
  assert.equal(servers.agenthub_network.env.AGENTHUB_WORKDIR, undefined);
});

test('only the builtins this image ships and this session enabled are rendered', () => {
  // The OpenClaw image carries files/ and network/ but neither browser/ nor sessions/, while the
  // enabling flags are instance-wide. A server pointing at a missing module would fail every turn.
  const root = runtimeWith(['files']);
  const servers = mcp.builtinServers(builtinEnv(root, {
    AGENTHUB_BROWSER_ENABLED: '1', AGENTHUB_SPAWN_MCP_ENABLED: '1'
  }));

  assert.deepEqual(Object.keys(servers), ['agenthub_files']);

  const disabled = mcp.builtinServers(builtinEnv(runtimeWith(['files', 'network']), {
    AGENTHUB_FILES_MCP_ENABLED: '0', AGENTHUB_NETWORK_MCP_ENABLED: '0'
  }));
  assert.deepEqual(disabled, {});
});

test('a server the agent added itself survives, a stale managed one does not', () => {
  // ~/.openclaw is restored from the session's own state tar, so the config comes back holding what
  // the previous incarnation wrote.
  const config = {
    mcp: {
      servers: {
        agent_added: { enabled: true, command: 'node' },
        stale: { enabled: true, command: 'node', [mcp.MANAGED_FLAG]: true }
      }
    },
    agents: { defaults: { model: 'x' } }
  };

  const merged = mcp.mergeServers(config, { fresh: { enabled: true, command: 'node' } });

  assert.deepEqual(Object.keys(merged.mcp.servers).sort(), ['agent_added', 'fresh']);
  assert.equal(merged.mcp.servers.fresh[mcp.MANAGED_FLAG], true);
  assert.equal(merged.mcp.servers.agent_added[mcp.MANAGED_FLAG], undefined);
  // Unrelated configuration must survive untouched.
  assert.deepEqual(merged.agents, { defaults: { model: 'x' } });
});

test('the managed marker sits on the server, never at the config root', () => {
  // An unknown root key makes OpenClaw reject the whole file ("<root>: Invalid input"), while an
  // unknown key inside a server entry validates cleanly.
  const merged = mcp.mergeServers({}, { s: { enabled: true, command: 'node' } });

  assert.deepEqual(Object.keys(merged), ['mcp']);
  assert.equal(merged[mcp.MANAGED_FLAG], undefined);
  assert.equal(merged.mcp.servers.s[mcp.MANAGED_FLAG], true);
});

test('a session with no MCP leaves no empty scaffolding behind', () => {
  const merged = mcp.mergeServers({
    mcp: { servers: { stale: { enabled: true, command: 'node', [mcp.MANAGED_FLAG]: true } } }
  }, {});

  assert.equal(merged.mcp, undefined);

  // Other mcp.* settings must not be dropped with the servers.
  const keepsSiblings = mcp.mergeServers({
    mcp: { servers: { stale: { command: 'node', [mcp.MANAGED_FLAG]: true } }, somethingElse: true }
  }, {});
  assert.deepEqual(keepsSiblings.mcp, { somethingElse: true });
});

test('writeConfig merges builtins and user servers into the config file', () => {
  const root = runtimeWith(['files', 'network']);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-oc-cfg-'));
  const configPath = path.join(dir, 'openclaw.json');
  const userPath = path.join(dir, 'agenthub-mcp.json');
  fs.writeFileSync(userPath, JSON.stringify({
    mcpServers: { agenthub_files: { command: 'evil' }, user_tool: { command: 'node' } }
  }));

  mcp.writeConfig(configPath, userPath, ['agenthub_files', 'agenthub_network'], builtinEnv(root));
  const written = JSON.parse(fs.readFileSync(configPath, 'utf8'));

  assert.deepEqual(Object.keys(written.mcp.servers).sort(),
    ['agenthub_files', 'agenthub_network', 'user_tool']);
  // The reserved entry is the runtime's own, not the one the user config tried to put there.
  assert.notEqual(written.mcp.servers.agenthub_files.command, 'evil');
});

test('writeConfig survives a config file that is missing or corrupt', () => {
  const root = runtimeWith(['files']);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-oc-cfg2-'));

  for (const contents of [undefined, 'not json', '[]']) {
    const configPath = path.join(dir, `${String(contents)}.json`);
    if (contents !== undefined) fs.writeFileSync(configPath, contents);
    mcp.writeConfig(configPath, '', [], builtinEnv(root));
    assert.ok(JSON.parse(fs.readFileSync(configPath, 'utf8')).mcp.servers.agenthub_files);
  }
});

test('the openclaw entrypoint wires MCP and reserves the runtime-owned names', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'openclaw', 'entrypoint.sh'), 'utf8');

  assert.match(entrypoint,
    /node "\$RUNTIME\/openclaw\/mcp-config\.js" "\$OPENCLAW_CONFIG_PATH" "\$\{AGENTHUB_MCP_CONFIG:-\}"/);
  assert.match(entrypoint,
    /agenthub_browser agenthub_sessions agenthub_files agenthub_network/);
  // OPENCLAW_CONFIG_PATH is honoured by the CLI, so the file written is the one it reads.
  assert.match(entrypoint, /export OPENCLAW_CONFIG_PATH=/);
});

test('a builtin the image does not ship is skipped instead of aborting the entrypoint', () => {
  const common = fs.readFileSync(
    path.join(runtimeDir, 'common', 'entrypoint-common.sh'), 'utf8');

  // Reproduced: with the spawn MCP enabled, the OpenClaw image has no sessions/configure.mjs, and
  // the unconditional `node` exited MODULE_NOT_FOUND, which under `set -e` killed the entrypoint
  // before the agent ever started.
  assert.match(common, /merge_builtin_mcp\(\) \{/);
  assert.match(common, /if \[ ! -f "\$module" \]; then/);
  for (const builtin of ['browser configure-claude.mjs', 'sessions configure.mjs',
    'network configure.mjs', 'files configure.mjs']) {
    assert.match(common, new RegExp(`merge_builtin_mcp ${builtin.replace('.', '\\.')}`));
  }
});
