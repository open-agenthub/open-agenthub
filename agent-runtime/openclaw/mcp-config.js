'use strict';

const fs = require('node:fs');
const path = require('node:path');

/**
 * Writes the session's MCP servers into OpenClaw's own config — `mcp.servers` in
 * `~/.openclaw/openclaw.json`. OpenClaw was the one runtime with no MCP wiring at all: its
 * entrypoint configured nothing and its driver passes no config flag, so a session's MCP servers
 * simply did not exist for it.
 *
 * Verified against the pinned CLI (2026.7.1-2) with `openclaw mcp list` / `openclaw mcp probe` and
 * `openclaw config validate`.
 */

const SERVER_NAME = /^[A-Za-z0-9_-]+$/;
const ENV_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;
const HEADER_NAME = /^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/;
const SERVER_FIELDS = new Set([
  'type', 'command', 'args', 'env', 'url', 'headers', 'envHeaders',
  'bearerTokenEnvVar', 'enabled', 'enabledTools', 'disabledTools'
]);

/**
 * Marks the entries this module owns, so they can be withdrawn on the next start.
 *
 * Per server, not at the root: an unknown root key makes OpenClaw reject the whole file
 * ("<root>: Invalid input" from `openclaw config validate`), while an unknown key inside a server
 * entry validates cleanly. Bookkeeping is needed at all because `~/.openclaw` is the state
 * directory and is restored from the session's own tar, so the file comes back holding whatever the
 * previous incarnation wrote — and anything the agent added itself with `openclaw mcp add` has to
 * survive.
 */
const MANAGED_FLAG = 'agenthubManaged';

function record(value, label) {
  if (value === null || Array.isArray(value) || typeof value !== 'object') {
    throw new Error(label + ' must be an object');
  }
  return value;
}

function stringValue(value, label) {
  if (typeof value !== 'string' || value.length === 0) throw new Error(label + ' must be a non-empty string');
  return value;
}

function stringArray(value, label) {
  if (!Array.isArray(value)) throw new Error(label + ' must be an array');
  const result = value.map(item => stringValue(item, label + ' item'));
  if (new Set(result).size !== result.length) throw new Error(label + ' contains duplicate values');
  return result;
}

function envName(value, label) {
  if (typeof value !== 'string' || !ENV_NAME.test(value)) throw new Error(label + ' must be an environment variable name');
  return value;
}

function convertServer(name, input) {
  if (!SERVER_NAME.test(name)) throw new Error('Invalid server name');
  const server = record(input, 'MCP server');
  for (const key of Object.keys(server)) {
    if (!SERVER_FIELDS.has(key)) throw new Error('Unsupported MCP server field');
  }

  const hasCommand = server.command !== undefined;
  const hasUrl = server.url !== undefined;
  if (hasCommand && hasUrl) throw new Error('MCP server is ambiguous: both command and URL are set');
  let type = server.type;
  if (type === undefined) type = hasCommand ? 'stdio' : hasUrl ? 'http' : undefined;
  if (type === 'streamable-http') type = 'http';
  if (type !== 'stdio' && type !== 'http') throw new Error('Unsupported transport');

  // Enabled unless the caller disabled it. Stated rather than left out: the default is not ours to
  // rely on, and `openclaw mcp add --disabled` shows the field is what decides.
  const out = { enabled: server.enabled === undefined ? true : server.enabled };
  if (typeof out.enabled !== 'boolean') throw new Error('enabled must be a boolean');
  // OpenClaw filters tools with toolFilter.include / .exclude, not with the flat lists the
  // AgentHub document uses.
  const filter = {};
  if (server.enabledTools !== undefined) filter.include = stringArray(server.enabledTools, 'enabledTools');
  if (server.disabledTools !== undefined) filter.exclude = stringArray(server.disabledTools, 'disabledTools');
  if (Object.keys(filter).length) out.toolFilter = filter;

  if (type === 'stdio') {
    if (hasUrl || server.headers !== undefined || server.envHeaders !== undefined ||
        server.bearerTokenEnvVar !== undefined) {
      throw new Error('stdio transport contains HTTP-only fields');
    }
    // No explicit transport: a command is what makes an entry stdio, confirmed by a server
    // configured with command/args alone being listed and spawned.
    out.command = stringValue(server.command, 'command');
    if (server.args !== undefined) out.args = stringArray(server.args, 'args');
    if (server.env !== undefined) {
      const values = record(server.env, 'env');
      const env = {};
      for (const [key, value] of Object.entries(values)) {
        envName(key, 'env key');
        env[key] = stringValue(value, 'env value');
      }
      out.env = env;
    }
    return out;
  }

  if (hasCommand || server.args !== undefined || server.env !== undefined) {
    throw new Error('HTTP transport contains stdio-only fields');
  }
  const urlText = stringValue(server.url, 'url');
  let url;
  try { url = new URL(urlText); } catch { throw new Error('URL must be valid HTTP or HTTPS'); }
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error('URL must be valid HTTP or HTTPS');
  if (url.username || url.password) throw new Error('URL credentials are unsafe');
  out.url = urlText;
  out.transport = 'streamable-http';

  const headers = {};
  const seenHeaders = new Set();
  let bearer = server.bearerTokenEnvVar === undefined ? undefined :
    envName(server.bearerTokenEnvVar, 'bearerTokenEnvVar');

  if (server.envHeaders !== undefined) {
    for (const [header, variable] of Object.entries(record(server.envHeaders, 'envHeaders'))) {
      if (!HEADER_NAME.test(header)) throw new Error('Invalid HTTP header name');
      const canonicalHeader = header.toLowerCase();
      if (seenHeaders.has(canonicalHeader)) throw new Error('Ambiguous duplicate header source');
      if (canonicalHeader === 'authorization' && bearer) throw new Error('Ambiguous duplicate bearer token source');
      seenHeaders.add(canonicalHeader);
      if (canonicalHeader === 'authorization') {
        bearer = envName(variable, 'envHeaders value');
      } else {
        headers[header] = '${' + envName(variable, 'envHeaders value') + '}';
      }
    }
  }
  if (server.headers !== undefined) {
    for (const [header, value] of Object.entries(record(server.headers, 'headers'))) {
      if (!HEADER_NAME.test(header)) throw new Error('Invalid HTTP header name');
      stringValue(value, 'header value');
      const canonicalHeader = header.toLowerCase();
      if (seenHeaders.has(canonicalHeader)) throw new Error('Ambiguous duplicate header source');
      seenHeaders.add(canonicalHeader);
      const authorization = canonicalHeader === 'authorization';
      const environmentMatch = /^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$/.exec(value);
      const bearerMatch = /^Bearer \$\{([A-Za-z_][A-Za-z0-9_]*)\}$/.exec(value);
      if (authorization) {
        if (!bearerMatch) throw new Error('Literal Authorization secrets are not allowed');
        if (bearer) throw new Error('Ambiguous duplicate bearer token source');
        bearer = bearerMatch[1];
        headers[header] = 'Bearer ${' + bearer + '}';
      } else if (environmentMatch) {
        headers[header] = '${' + environmentMatch[1] + '}';
      } else {
        headers[header] = value;
      }
    }
  }
  if (bearer && !Object.keys(headers).some(h => h.toLowerCase() === 'authorization')) {
    headers.Authorization = 'Bearer ${' + bearer + '}';
  }
  if (Object.keys(headers).length) out.headers = headers;
  return out;
}

/** Converts an AgentHub MCP document to OpenClaw server entries, dropping runtime-owned names. */
function convertMcp(agentHubJson, reservedServers = []) {
  let parsed = agentHubJson;
  if (typeof agentHubJson === 'string') {
    try { parsed = JSON.parse(agentHubJson); } catch { throw new Error('MCP configuration must be valid JSON'); }
  }
  const root = record(parsed, 'MCP configuration');
  if (Object.keys(root).some(key => key !== 'mcpServers')) {
    throw new Error('Unsupported top-level security configuration');
  }
  const servers = record(root.mcpServers, 'mcpServers');
  const reserved = new Set(reservedServers);
  const out = {};
  for (const name of Object.keys(servers).sort()) {
    if (reserved.has(name)) continue;
    out[name] = convertServer(name, servers[name]);
  }
  return out;
}

// Only the servers the OpenClaw image actually ships. It carries files/ and network/ but neither
// browser/ nor sessions/, so rendering those would point OpenClaw at paths that do not exist.
const BUILTIN_SERVERS = [
  { name: 'agenthub_files', dir: 'files', flag: 'AGENTHUB_FILES_MCP_ENABLED', extraEnv: ['AGENTHUB_WORKDIR', 'AGENTHUB_FILE_ROOT'] },
  { name: 'agenthub_network', dir: 'network', flag: 'AGENTHUB_NETWORK_MCP_ENABLED', extraEnv: [] }
];

/**
 * The runtime-owned servers, as OpenClaw entries.
 *
 * OpenClaw spawns an MCP server with a cleared environment — a probe child saw two variables and
 * neither RUNTIME nor AGENTHUB_CALLBACK_TOKEN — so every variable the server needs has to be stated
 * here. They are written as `${NAME}` references, not values: OpenClaw interpolates those from its
 * own environment (verified: a `${AGENTHUB_CALLBACK_TOKEN}` reference reached the child as the real
 * token, a literal passed through unchanged). That matters because `~/.openclaw` is archived into
 * the session's state tar and uploaded, so a literal token here would be a credential leaving the
 * pod.
 */
function builtinServers(env = process.env, nodeBin = process.execPath) {
  const runtime = env.RUNTIME || '/opt/session-agent';
  const servers = {};
  for (const server of BUILTIN_SERVERS) {
    if (env[server.flag] !== '1') continue;
    const module = `${runtime}/${server.dir}/server.mjs`;
    // Guarded the same way the shared entrypoint guards its own builtins: a custom image may not
    // ship one, and a server pointing at a missing file fails on every turn instead of once.
    if (!fs.existsSync(module)) continue;
    // Only variables that are actually set. OpenClaw resolves each reference when it loads the
    // config and reports an unset one as `Missing env var "X" - feature using this value will be
    // unavailable`, which turns an optional variable such as AGENTHUB_FILE_ROOT into a warning on
    // every command for no reason.
    const names = ['PATH', 'RUNTIME', 'AGENTHUB_CALLBACK_URL', 'AGENTHUB_CALLBACK_TOKEN',
      ...server.extraEnv].filter(name => env[name] !== undefined && env[name] !== '');
    servers[server.name] = {
      enabled: true,
      command: nodeBin,
      args: [module],
      env: Object.fromEntries(names.map(name => [name, '${' + name + '}']))
    };
  }
  return servers;
}

/**
 * Merges servers into an existing OpenClaw config, withdrawing the ones a previous start wrote and
 * leaving everything else — including servers the agent added itself — alone.
 */
function mergeServers(config, servers) {
  const base = config && typeof config === 'object' && !Array.isArray(config) ? config : {};
  const mcp = base.mcp && typeof base.mcp === 'object' && !Array.isArray(base.mcp) ? base.mcp : {};
  const existing = mcp.servers && typeof mcp.servers === 'object' && !Array.isArray(mcp.servers)
    ? mcp.servers : {};

  const kept = Object.fromEntries(Object.entries(existing)
    .filter(([, entry]) => !(entry && typeof entry === 'object' && entry[MANAGED_FLAG] === true)));
  const managed = Object.fromEntries(Object.entries(servers)
    .map(([name, entry]) => [name, { ...entry, [MANAGED_FLAG]: true }]));
  const merged = { ...kept, ...managed };

  const nextMcp = { ...mcp };
  if (Object.keys(merged).length === 0) delete nextMcp.servers;
  else nextMcp.servers = merged;

  const out = { ...base };
  if (Object.keys(nextMcp).length === 0) delete out.mcp;
  else out.mcp = nextMcp;
  return out;
}

function readJson(file) {
  if (!file || !fs.existsSync(file)) return {};
  try {
    const parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  } catch { return {}; }
}

/**
 * Rewrites `mcp.servers` from the session's effective MCP document plus the enabled builtins.
 *
 * A malformed user document is fatal, matching the Codex and Cursor converters: a session that
 * silently drops the servers it was configured with looks like the tools are broken.
 */
function writeConfig(configPath, userMcpPath, reservedServers = [], env = process.env) {
  const userServers = userMcpPath && fs.existsSync(userMcpPath)
    ? convertMcp(fs.readFileSync(userMcpPath, 'utf8'), reservedServers)
    : {};
  const merged = mergeServers(readJson(configPath), { ...userServers, ...builtinServers(env) });
  fs.mkdirSync(path.dirname(configPath), { recursive: true });
  fs.writeFileSync(configPath, `${JSON.stringify(merged, null, 2)}\n`, { mode: 0o600 });
  return configPath;
}

if (require.main === module) {
  try {
    writeConfig(process.argv[2], process.argv[3] || '', process.argv.slice(4));
  } catch (error) {
    console.error('[openclaw-mcp] MCP configuration rejected: ' + error.message);
    process.exit(1);
  }
}

module.exports = {
  convertMcp, builtinServers, mergeServers, writeConfig, MANAGED_FLAG, BUILTIN_SERVERS
};
