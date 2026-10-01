'use strict';

const fs = require('node:fs');

const SERVER_NAME = /^[A-Za-z0-9_-]+$/;
const ENV_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;
const HEADER_NAME = /^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/;
const SERVER_FIELDS = new Set([
  'type', 'command', 'args', 'env', 'url', 'headers', 'envHeaders',
  'bearerTokenEnvVar', 'enabled', 'enabledTools', 'disabledTools'
]);

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

function quoted(value) { return JSON.stringify(value); }
function array(values) { return '[' + values.map(quoted).join(', ') + ']'; }
function inlineTable(values) {
  return '{ ' + Object.keys(values).sort().map(key => quoted(key) + ' = ' + quoted(values[key])).join(', ') + ' }';
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

  const lines = ['[mcp_servers.' + name + ']'];
  if (server.enabled !== undefined) {
    if (typeof server.enabled !== 'boolean') throw new Error('enabled must be a boolean');
    lines.push('enabled = ' + server.enabled);
  }
  if (server.enabledTools !== undefined) lines.push('enabled_tools = ' + array(stringArray(server.enabledTools, 'enabledTools')));
  if (server.disabledTools !== undefined) lines.push('disabled_tools = ' + array(stringArray(server.disabledTools, 'disabledTools')));

  if (type === 'stdio') {
    if (hasUrl || server.headers !== undefined || server.envHeaders !== undefined || server.bearerTokenEnvVar !== undefined) {
      throw new Error('stdio transport contains HTTP-only fields');
    }
    lines.push('command = ' + quoted(stringValue(server.command, 'command')));
    if (server.args !== undefined) lines.push('args = ' + array(stringArray(server.args, 'args')));
    if (server.env !== undefined) {
      const values = record(server.env, 'env');
      for (const [key, value] of Object.entries(values)) {
        envName(key, 'env key');
        stringValue(value, 'env value');
      }
      lines.push('env = ' + inlineTable(values));
    }
    return lines.join('\n');
  }

  if (hasCommand || server.args !== undefined || server.env !== undefined) {
    throw new Error('HTTP transport contains stdio-only fields');
  }
  const urlText = stringValue(server.url, 'url');
  let url;
  try { url = new URL(urlText); } catch { throw new Error('URL must be valid HTTP or HTTPS'); }
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error('URL must be valid HTTP or HTTPS');
  if (url.username || url.password) throw new Error('URL credentials are unsafe');
  lines.push('url = ' + quoted(urlText));

  let bearer = server.bearerTokenEnvVar === undefined ? undefined :
    envName(server.bearerTokenEnvVar, 'bearerTokenEnvVar');
  const literalHeaders = {};
  const environmentHeaders = {};
  const seenHeaders = new Set();
  if (server.envHeaders !== undefined) {
    for (const [header, variable] of Object.entries(record(server.envHeaders, 'envHeaders'))) {
      if (!HEADER_NAME.test(header)) throw new Error('Invalid HTTP header name');
      const canonicalHeader = header.toLowerCase();
      if (seenHeaders.has(canonicalHeader)) throw new Error('Ambiguous duplicate header source');
      if (canonicalHeader === 'authorization' && bearer) throw new Error('Ambiguous duplicate bearer token source');
      seenHeaders.add(canonicalHeader);
      environmentHeaders[header] = envName(variable, 'envHeaders value');
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
      } else if (environmentMatch) {
        environmentHeaders[header] = environmentMatch[1];
      } else {
        literalHeaders[header] = value;
      }
    }
  }
  if (bearer) lines.push('bearer_token_env_var = ' + quoted(bearer));
  if (Object.keys(literalHeaders).length) lines.push('http_headers = ' + inlineTable(literalHeaders));
  if (Object.keys(environmentHeaders).length) lines.push('env_http_headers = ' + inlineTable(environmentHeaders));
  return lines.join('\n');
}

const SKILL_LIBRARY = 'skill-library';

/** True for the http entry the hub injects for its skill-library server. */
function isHubSkillLibrary(entry) {
  if (!entry || typeof entry !== 'object' || Array.isArray(entry)) return false;
  if (typeof entry.url !== 'string' || entry.command !== undefined) return false;
  return entry.type === undefined || entry.type === 'http' || entry.type === 'streamable-http';
}

function convertMcp(agentHubJson, reservedServers = [], env = process.env, nodeBin = process.execPath) {
  let parsed = agentHubJson;
  if (typeof agentHubJson === 'string') {
    try { parsed = JSON.parse(agentHubJson); } catch { throw new Error('MCP configuration must be valid JSON'); }
  }
  const root = record(parsed, 'MCP configuration');
  const keys = Object.keys(root);
  if (keys.some(key => key !== 'mcpServers')) throw new Error('Unsupported top-level security configuration');
  const servers = record(root.mcpServers, 'mcpServers');
  const reserved = new Set(reservedServers);
  const names = Object.keys(servers).filter(name => !reserved.has(name)).sort();
  // The hub's skill-library server is replaced by the local stdio proxy, which adds the
  // path and out_dir arguments and forwards everything else to that same url. Keyed off
  // the injected entry, so it follows the hub's own switch for the feature.
  const useSkillProxy = env.AGENTHUB_SKILLS_MCP_ENABLED === '1';
  const blocks = names.map(name =>
    useSkillProxy && name === SKILL_LIBRARY && isHubSkillLibrary(servers[name])
      ? skillProxyToml(env, nodeBin)
      : convertServer(name, servers[name]));
  return blocks.join('\n\n') + (blocks.length ? '\n' : '');
}

/** Codex needs an absolute interpreter and an explicit list of variables to forward. */
function skillProxyToml(env = process.env, nodeBin = process.execPath) {
  const runtime = env.RUNTIME || '/opt/session-agent';
  return [
    '[mcp_servers.' + SKILL_LIBRARY + ']',
    'command = ' + quoted(nodeBin),
    'args = ' + array([runtime + '/skills/server.mjs']),
    'env_vars = ' + array(['PATH', 'RUNTIME', 'TMPDIR', 'AGENTHUB_CALLBACK_URL', 'AGENTHUB_CALLBACK_TOKEN'])
  ].join('\n');
}

// Codex starts MCP subprocesses with a cleared environment and forwards only the
// variables named in env_vars, so the interpreter must be an absolute path and
// PATH/RUNTIME must be forwarded explicitly or the server dies before initialize.
const BUILTIN_SERVERS = [
  { name: 'agenthub_browser', dir: 'browser', flag: 'AGENTHUB_BROWSER_ENABLED', extraEnv: [] },
  { name: 'agenthub_sessions', dir: 'sessions', flag: 'AGENTHUB_SPAWN_MCP_ENABLED', extraEnv: ['AGENTHUB_SESSION_ID'] },
  { name: 'agenthub_files', dir: 'files', flag: 'AGENTHUB_FILES_MCP_ENABLED', extraEnv: ['AGENTHUB_WORKDIR', 'AGENTHUB_FILE_ROOT'] },
  { name: 'agenthub_network', dir: 'network', flag: 'AGENTHUB_NETWORK_MCP_ENABLED', extraEnv: [] }
];

function builtinToml(env = process.env, nodeBin = process.execPath) {
  const runtime = env.RUNTIME || '/opt/session-agent';
  const blocks = BUILTIN_SERVERS
    .filter(server => env[server.flag] === '1')
    .map(server => [
      '[mcp_servers.' + server.name + ']',
      'command = ' + quoted(nodeBin),
      'args = ' + array([runtime + '/' + server.dir + '/server.mjs']),
      'env_vars = ' + array([
        'PATH', 'RUNTIME', 'AGENTHUB_CALLBACK_URL', 'AGENTHUB_CALLBACK_TOKEN',
        ...server.extraEnv
      ])
    ].join('\n'));
  return blocks.join('\n\n') + (blocks.length ? '\n' : '');
}

if (require.main === module) {
  if (process.argv[2] === '--builtin') {
    process.stdout.write(builtinToml());
  } else {
    try {
      process.stdout.write(convertMcp(fs.readFileSync(process.argv[2], 'utf8'), process.argv.slice(3)));
    } catch (error) {
      console.error('[codex-mcp] MCP configuration rejected: ' + error.message);
      process.exit(1);
    }
  }
}

module.exports = { convertMcp, builtinToml, skillProxyToml };
