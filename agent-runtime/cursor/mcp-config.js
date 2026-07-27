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

  const out = {};
  if (type === 'http') out.type = 'http';
  if (server.enabled !== undefined) {
    if (typeof server.enabled !== 'boolean') throw new Error('enabled must be a boolean');
    out.enabled = server.enabled;
  }
  if (server.enabledTools !== undefined) out.enabledTools = stringArray(server.enabledTools, 'enabledTools');
  if (server.disabledTools !== undefined) out.disabledTools = stringArray(server.disabledTools, 'disabledTools');

  if (type === 'stdio') {
    if (hasUrl || server.headers !== undefined || server.envHeaders !== undefined ||
        server.bearerTokenEnvVar !== undefined) {
      throw new Error('stdio transport contains HTTP-only fields');
    }
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

function convertMcp(agentHubJson, reservedServers = []) {
  let parsed = agentHubJson;
  if (typeof agentHubJson === 'string') {
    try { parsed = JSON.parse(agentHubJson); } catch { throw new Error('MCP configuration must be valid JSON'); }
  }
  const root = record(parsed, 'MCP configuration');
  const keys = Object.keys(root);
  if (keys.some(key => key !== 'mcpServers')) throw new Error('Unsupported top-level security configuration');
  const servers = record(root.mcpServers, 'mcpServers');
  const reserved = new Set(reservedServers);
  const mcpServers = {};
  for (const name of Object.keys(servers).sort()) {
    if (reserved.has(name)) continue;
    mcpServers[name] = convertServer(name, servers[name]);
  }
  return JSON.stringify({ mcpServers }, null, 2) + '\n';
}

if (require.main === module) {
  try {
    process.stdout.write(convertMcp(fs.readFileSync(process.argv[2], 'utf8'), process.argv.slice(3)));
  } catch (error) {
    console.error('[cursor-mcp] MCP configuration rejected: ' + error.message);
    process.exit(1);
  }
}

module.exports = { convertMcp };
