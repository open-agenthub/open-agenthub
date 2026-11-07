'use strict';

// Claude Code's own model list (#461): the answer to the SDK control request "initialize" on a
// headless stream-json CLI. With a subscription login the CLI shapes it by the account's plan and
// organisation (it adds the variants the account may use from its bootstrap answer, cached in
// ~/.claude.json); with an API key it is the version's list and says nothing about entitlement.
//
// The probe CLI must not act as a second agent: no settings sources (they hold the session's hooks,
// which would report a SessionStart/SessionEnd to the hub), no plugins (the fleet mod would compete
// for the session's inbox), no MCP servers and no transcript.

const PROBE_ARGS = ['-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose',
  '--setting-sources', '', '--no-session-persistence', '--strict-mcp-config'];
const REQUEST_ID = 'agenthub-models';

function parseInitialize(stdout) {
  for (const line of stdout.split('\n')) {
    if (!line.includes(REQUEST_ID)) continue;
    let message;
    try { message = JSON.parse(line); } catch { continue; }
    if (message.type !== 'control_response' || !message.response) continue;
    if (message.response.subtype === 'error') return { error: String(message.response.error || 'initialize failed') };
    return { response: message.response.response || {} };
  }
  return null;
}

function toModel(raw) {
  if (!raw || typeof raw !== 'object') return null;
  return {
    id: raw.value,
    label: raw.displayName,
    description: raw.description,
    resolvedId: raw.resolvedModel,
    isDefault: raw.value === 'default',
    capabilities: {
      reasoningEfforts: raw.supportsEffort ? raw.supportedEffortLevels : undefined,
      fastMode: typeof raw.supportsFastMode === 'boolean' ? raw.supportsFastMode : undefined,
      adaptiveThinking: typeof raw.supportsAdaptiveThinking === 'boolean' ? raw.supportsAdaptiveThinking : undefined
    }
  };
}

async function probe({ env, run }) {
  const probeEnv = { ...env };
  delete probeEnv.CLAUDE_CODE_PLUGIN_DIRS;
  const input = JSON.stringify({ type: 'control_request', request_id: REQUEST_ID, request: { subtype: 'initialize' } }) + '\n';
  const result = await run({ cmd: 'claude', args: PROBE_ARGS, env: probeEnv, input, onStdout: out => parseInitialize(out) !== null });
  const parsed = parseInitialize(result.stdout);
  const cliVersion = env.CLAUDE_CODE_VERSION;
  if (!parsed) {
    const reason = result.timedOut ? 'Claude Code did not answer in time.' : (result.stderr || (result.error && result.error.message) || 'Claude Code ended without a model list.');
    return { source: 'cli', state: 'error', cliVersion, error: { code: result.timedOut ? 'timeout' : 'probe_failed', message: reason } };
  }
  if (parsed.error) return { source: 'cli', state: 'error', cliVersion, error: { code: 'probe_failed', message: parsed.error } };
  const response = parsed.response;
  // The account block names the e-mail, organisation and plan; only whether a subscription login
  // shaped the list is kept.
  const account = response.account && typeof response.account === 'object' ? response.account : {};
  const subscription = Boolean(account.subscriptionType || account.organization || account.email);
  return {
    source: 'cli',
    verified: subscription,
    state: 'ok',
    cliVersion: response.claude_code_version || cliVersion,
    models: (Array.isArray(response.models) ? response.models : []).map(toModel)
  };
}

module.exports = { PROBE_ARGS, parseInitialize, probe, toModel };
