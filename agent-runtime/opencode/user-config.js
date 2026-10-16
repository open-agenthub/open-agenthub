'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const SERVER_NAME = /^[A-Za-z0-9_-]+$/;
const ENV_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;
const PLACEHOLDER = /\$\{([A-Za-z_][A-Za-z0-9_]*)\}/g;
const GO_PROVIDER = 'opencode-go';

// Coding-capable OpenCode Go models, best first. An ordered list rather than one pinned id: the Go
// catalogue changes every few weeks, and a renamed model should degrade to the next one here
// instead of leaving the session on a model that no longer exists. Cost matters too — Go limits
// usage by dollar value per window, so the first choice is a strong coder that does not spend the
// window in an afternoon.
const GO_MODEL_PREFERENCE = Object.freeze([
  'glm-5.3', 'kimi-k2.7-code', 'deepseek-v4-pro', 'kimi-k3', 'qwen3.8-max', 'minimax-m3'
]);

function warn(message) {
  process.stderr.write('[opencode-config] ' + message + '\n');
}

function record(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

// Claude-style "${VAR}" becomes OpenCode's "{env:VAR}", so a secret the hub passes by reference
// stays a reference instead of being resolved into a file under $HOME.
function placeholders(value) {
  return value.replace(PLACEHOLDER, (_, name) => '{env:' + name + '}');
}

function stringMap(value, keyPattern, label) {
  if (value === undefined) return undefined;
  if (!record(value)) throw new Error(label + ' must be an object');
  const out = {};
  for (const [key, item] of Object.entries(value)) {
    if (keyPattern && !keyPattern.test(key)) throw new Error(label + ' has an invalid key');
    if (typeof item !== 'string') throw new Error(label + ' values must be strings');
    out[key] = placeholders(item);
  }
  return out;
}

/** Converts one server of the hub's Claude-format MCP config into OpenCode's `mcp` shape. */
function convertServer(server) {
  if (!record(server)) throw new Error('server must be an object');
  const hasCommand = server.command !== undefined;
  const hasUrl = server.url !== undefined;
  if (hasCommand && hasUrl) throw new Error('both command and url are set');
  let type = server.type;
  if (type === undefined) type = hasCommand ? 'stdio' : hasUrl ? 'http' : undefined;

  const out = {};
  if (type === 'stdio') {
    if (typeof server.command !== 'string' || !server.command) throw new Error('command must be a string');
    const args = server.args === undefined ? [] : server.args;
    if (!Array.isArray(args) || args.some(arg => typeof arg !== 'string')) throw new Error('args must be strings');
    out.type = 'local';
    out.command = [server.command, ...args];
    const environment = stringMap(server.env, ENV_NAME, 'env');
    if (environment) out.environment = environment;
  } else if (type === 'http' || type === 'streamable-http' || type === 'sse') {
    let url;
    try { url = new URL(server.url); } catch { throw new Error('url must be valid'); }
    if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('url must be http or https');
    out.type = 'remote';
    out.url = server.url;
    const headers = stringMap(server.headers, null, 'headers');
    if (headers) out.headers = headers;
  } else {
    throw new Error('unsupported transport');
  }
  out.enabled = server.enabled !== false;
  return out;
}

/**
 * Every server of the effective MCP config, in OpenCode's shape. A server that cannot be converted
 * is left out with a warning rather than failing the start: one bad entry in a shared catalogue
 * should cost that server, not the whole session.
 */
function convertMcp(servers) {
  const out = {};
  if (!record(servers)) return out;
  for (const name of Object.keys(servers).sort()) {
    if (!SERVER_NAME.test(name)) {
      warn('skipping MCP server with an invalid name');
      continue;
    }
    try {
      out[name] = convertServer(servers[name]);
    } catch (error) {
      warn('skipping MCP server ' + name + ': ' + error.message);
    }
  }
  return out;
}

function readJson(file) {
  if (!file) return undefined;
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return undefined; }
}

/** The provider whose models the default should come from, or null to leave OpenCode's choice. */
function goProviderFor(env, auth) {
  if (env.AGENTHUB_AUTH_MODE === 'apikey') return env.OPENCODE_API_KEY ? GO_PROVIDER : null;
  // A subscription login may be for any provider OpenCode supports. Only an OpenCode Go login
  // gets a Go default; anything else is left to OpenCode, which picks among what is logged in.
  return record(auth) && record(auth[GO_PROVIDER]) ? GO_PROVIDER : null;
}

/** Picks the first preferred model the catalogue offers, else the first non-free one listed. */
function selectGoModel(catalogText) {
  const offered = String(catalogText || '').split(/\r?\n/)
    .map(line => line.trim())
    .filter(line => line.startsWith(GO_PROVIDER + '/'))
    .map(line => line.slice(GO_PROVIDER.length + 1));
  if (offered.length === 0) return null;
  const preferred = GO_MODEL_PREFERENCE.find(model => offered.includes(model));
  const fallback = offered.find(model => !model.endsWith('-free')) || offered[0];
  return GO_PROVIDER + '/' + (preferred || fallback);
}

function listGoModels(env) {
  return execFileSync('opencode', ['models', GO_PROVIDER], {
    env, encoding: 'utf8', timeout: 60_000, stdio: ['ignore', 'pipe', 'ignore']
  });
}

function defaultModel(env, auth, list = listGoModels) {
  const override = (env.AGENTHUB_OPENCODE_MODEL || '').trim();
  if (override) return override;
  if (!goProviderFor(env, auth)) return null;
  try {
    return selectGoModel(list(env));
  } catch {
    warn('could not list the OpenCode Go models; leaving the default model to OpenCode.');
    return null;
  }
}

function userConfig(env, deps = {}) {
  const mcpConfig = readJson(env.AGENTHUB_MCP_CONFIG);
  const auth = readJson(env.OPENCODE_AUTH_FILE);
  const config = { $schema: 'https://opencode.ai/config.json' };
  const model = defaultModel(env, auth, deps.listModels);
  if (model) config.model = model;
  const instructions = env.AGENTHUB_OPENCODE_INSTRUCTIONS;
  if (instructions && fs.existsSync(instructions)) config.instructions = [instructions];
  const mcp = convertMcp(record(mcpConfig) ? mcpConfig.mcpServers : undefined);
  if (Object.keys(mcp).length) config.mcp = mcp;
  return config;
}

function writeUserConfig(outputPath, env = process.env, deps = {}) {
  const config = userConfig(env, deps);
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, JSON.stringify(config, null, 2) + '\n', { mode: 0o600 });
  return config;
}

if (require.main === module) {
  const config = writeUserConfig(process.argv[2]);
  console.log('[opencode] model: ' + (config.model || 'OpenCode default') +
    ', MCP servers: ' + Object.keys(config.mcp || {}).length);
}

module.exports = {
  GO_MODEL_PREFERENCE, convertMcp, convertServer, defaultModel, goProviderFor, selectGoModel,
  userConfig, writeUserConfig
};
