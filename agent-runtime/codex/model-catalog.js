'use strict';

// Codex's own model list (#461): `model/list` on `codex app-server`. With a subscription login the
// CLI fetches the account's list from its service with that login; with an API key it answers from
// the list bundled into the version. A refused fetch is not an error in the answer — the CLI logs
// "failed to refresh available models" and returns the bundled list — so stderr is read to report
// that as a fallback instead of passing the bundled list off as the account's.

const REFRESH_FAILED = /failed to refresh available models:\s*([^\n]*)/;
const MAX_PAGES = 5;

function line(message) { return JSON.stringify(message) + '\n'; }

function toModel(raw) {
  if (!raw || typeof raw !== 'object' || raw.hidden === true) return null;
  const efforts = Array.isArray(raw.supportedReasoningEfforts)
    ? raw.supportedReasoningEfforts.map(e => e && e.reasoningEffort).filter(Boolean) : undefined;
  return {
    id: raw.id || raw.model,
    label: raw.displayName,
    description: raw.description,
    isDefault: raw.isDefault === true,
    capabilities: {
      reasoningEfforts: efforts,
      defaultEffort: raw.defaultReasoningEffort,
      imageInput: Array.isArray(raw.inputModalities) ? raw.inputModalities.includes('image') : undefined,
      fastMode: Array.isArray(raw.additionalSpeedTiers) ? raw.additionalSpeedTiers.includes('fast') : undefined
    }
  };
}

/** Lines of JSON-RPC answers by id. */
function answers(stdout) {
  const byId = new Map();
  for (const line of stdout.split('\n')) {
    if (!line.startsWith('{')) continue;
    try {
      const message = JSON.parse(line);
      if (message.id !== undefined && (message.result !== undefined || message.error !== undefined)) byId.set(message.id, message);
    } catch {}
  }
  return byId;
}

async function probe({ env, run }) {
  const models = [];
  let cursor = null;
  let stderr = '';
  let cliVersion = env.CODEX_VERSION;
  for (let page = 0; page < MAX_PAGES; page++) {
    const input = [
      { id: 1, method: 'initialize', params: { clientInfo: { name: 'agenthub-models', version: '1' } } },
      { method: 'initialized' },
      { id: 2, method: 'model/list', params: { includeHidden: false, limit: 100, cursor } }
    ].map(line).join('');
    const result = await run({ cmd: 'codex', args: ['app-server'], input, onStdout: out => answers(out).has(2) });
    stderr += result.stderr;
    const answer = answers(result.stdout).get(2);
    if (!answer) {
      return { source: 'cli', state: 'error', cliVersion, error: { code: result.timedOut ? 'timeout' : 'probe_failed',
        message: result.timedOut ? 'Codex did not answer in time.' : (result.stderr.split('\n').filter(l => /error/i.test(l)).pop() || 'Codex ended without a model list.') } };
    }
    if (answer.error) return { source: 'cli', state: 'error', cliVersion, error: { code: 'probe_failed', message: String(answer.error.message || 'model/list failed') } };
    const init = answers(result.stdout).get(1);
    const agent = init && init.result && /\/(\d+\.\d+\.\d+)/.exec(String(init.result.userAgent || ''));
    if (agent) cliVersion = agent[1];
    models.push(...(Array.isArray(answer.result.data) ? answer.result.data : []).map(toModel));
    cursor = answer.result.nextCursor || null;
    if (!cursor) break;
  }
  const subscription = env.AGENTHUB_AUTH_MODE !== 'apikey';
  const refused = REFRESH_FAILED.exec(stderr.replace(/\x1b\[[0-9;]*m/g, ''));
  if (refused && subscription) {
    const forbidden = /\b(401|403)\b/.test(refused[1]);
    return { source: 'cli', verified: false, state: 'fallback', cliVersion, models,
      error: { code: forbidden ? 'not_entitled' : 'refresh_failed', message: 'Codex could not load this account\'s model list and shows its built-in list instead: ' + refused[1] } };
  }
  return { source: 'cli', verified: subscription, state: 'ok', cliVersion, models };
}

module.exports = { REFRESH_FAILED, answers, probe, toModel };
