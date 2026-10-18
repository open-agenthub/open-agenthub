'use strict';

// Codex on a self-hosted model server (docs/custom-model-endpoints.md). Codex takes no environment
// variable for a base URL; it needs a provider table in config.toml and the top-level keys that
// select it. Printed to stdout for the entrypoint to place directly after the first line of
// config.toml: a top-level key written after any [table] header would belong to that table, and the
// MCP server tables follow.

const { settings, FIRST_TOKEN_TIMEOUT_MS, KEY_ENV } = require('../common/model-endpoint');

const PROVIDER_ID = 'agenthub_endpoint';

// JSON's string escapes are a subset of TOML basic-string escapes, so a quote or backslash in a
// model id or URL cannot end the value early.
function quoted(value) { return JSON.stringify(value); }

function providerToml(env = process.env) {
  const { url, model, key } = settings(env);
  const lines = [
    `model = ${quoted(model)}`,
    `model_provider = ${quoted(PROVIDER_ID)}`,
    '',
    `[model_providers.${PROVIDER_ID}]`,
    `name = ${quoted('Custom endpoint')}`,
    `base_url = ${quoted(url)}`,
    // Verified on the pinned 0.160.0: "chat" is refused when the config loads ("no longer
    // supported"), so a server without the Responses API cannot serve Codex at all.
    `wire_api = ${quoted('responses')}`,
    // See FIRST_TOKEN_TIMEOUT_MS: Codex's documented default of 5 minutes is shorter than a first turn.
    `stream_idle_timeout_ms = ${FIRST_TOKEN_TIMEOUT_MS}`
  ];
  // Only with a key: Codex fails every request with "missing environment variable" when env_key
  // names a variable that is unset, which is how a keyless server would otherwise look.
  if (key) lines.push(`env_key = ${quoted(KEY_ENV)}`);
  return lines.join('\n') + '\n';
}

if (require.main === module) process.stdout.write(providerToml());

module.exports = { PROVIDER_ID, providerToml };
