'use strict';

/**
 * Picks the default model for OpenClaw's API-key mode.
 *
 * OpenClaw ships `openai/gpt-5.5` as its default and keeps it whatever credentials are present.
 * ApiKey mode mounts exactly one provider key, so a session started with an Anthropic key came up
 * pointing at OpenAI, failed authentication on the first turn and then reported "no models
 * available" — the key was never the problem, the selected model was. Nothing in the runtime
 * moved that default, so the mode only ever worked for OpenAI by accident.
 *
 * Verified against the pinned CLI (2026.7.1-2) in the published image:
 *   - with only ANTHROPIC_API_KEY set, `openclaw models status --plain` reports
 *     `openai/gpt-5.5` and `openclaw models auth list` reports no profiles;
 *   - after `openclaw models set anthropic/claude-sonnet-5` the same status reports
 *     `anthropic effective=env:sk...  | source=env: ANTHROPIC_API_KEY`.
 *
 * So the env key is enough on its own — it needs a default model whose provider it can serve.
 * No `models auth paste-api-key` is required, and writing one would put the key into the agent's
 * SQLite auth store, which the entrypoint deliberately scrubs on every start.
 */

// The key the pod mounts decides the provider; AGENTHUB_OPENCLAW_API_KEY_SOURCE is not passed
// down, so this is derived from which variable is actually present.
const PROVIDER_BY_ENV = Object.freeze({
  ANTHROPIC_API_KEY: 'anthropic',
  OPENAI_API_KEY: 'openai',
  CURSOR_API_KEY: 'cursor'
});

/**
 * Ordered preference, not a pin. A hardcoded single id would silently rot the first time the
 * catalogue drops it, so these are tried in turn and anything the catalogue still offers wins
 * over an exact match that no longer exists.
 */
const PREFERRED = Object.freeze({
  anthropic: ['claude-sonnet-5', 'claude-opus-4-8', 'claude-sonnet-4-6', 'claude-haiku-4-5'],
  openai: ['gpt-5.5'],
  cursor: []
});

function providerFor(env) {
  for (const [name, provider] of Object.entries(PROVIDER_BY_ENV)) {
    if (env[name]) return provider;
  }
  return null;
}

/** Catalogue lines as printed by `openclaw models list --plain --provider <p>`. */
function parseCatalog(text, provider) {
  return String(text || '')
    .split('\n')
    .map(line => line.trim())
    .filter(line => line.startsWith(`${provider}/`));
}

function selectModel(catalog, provider, override) {
  if (override) return override;
  if (catalog.length === 0) return null;
  for (const candidate of PREFERRED[provider] || []) {
    const match = catalog.find(id => id === `${provider}/${candidate}`);
    if (match) return match;
  }
  // The catalogue moved past every preference. Taking the first entry keeps the session usable
  // and is still a model of the provider whose key is mounted, which is the property that matters.
  return catalog[0];
}

function main(argv = process.argv.slice(2), env = process.env, stdin = '') {
  const provider = argv[0] || providerFor(env);
  if (!provider) {
    process.stderr.write('select-model: no provider API key is present\n');
    return 1;
  }

  const model = selectModel(parseCatalog(stdin, provider), provider, env.AGENTHUB_OPENCLAW_MODEL);
  if (!model) {
    // The Cursor case: `models list --all --provider cursor` is empty in the pinned build with
    // and without a key, so a Cursor-keyed session can never reach a usable model. Saying so here
    // beats letting the TUI come up and report "no models available" with no cause given.
    process.stderr.write(
      `select-model: OpenClaw offers no models for provider "${provider}" in this build\n`);
    return 1;
  }

  process.stdout.write(model);
  return 0;
}

if (require.main === module) {
  const chunks = [];
  process.stdin.on('data', chunk => chunks.push(chunk));
  process.stdin.on('end', () => {
    process.exit(main(process.argv.slice(2), process.env, Buffer.concat(chunks).toString('utf8')));
  });
}

module.exports = { PROVIDER_BY_ENV, PREFERRED, providerFor, parseCatalog, selectModel, main };
