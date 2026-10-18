'use strict';

// A session on a self-hosted model server (docs/custom-model-endpoints.md). The backend hands the
// pod three provider-neutral variables; this module turns them into what each CLI reads and checks,
// before the CLI starts, that the server speaks the protocol that CLI needs.
//
// The check exists because none of the three CLIs fails usefully on its own. Claude Code against an
// OpenAI-only server, or a server the network policy does not let the pod reach, retries until the
// session looks hung; the person watching sees a spinner, not a reason.

const fs = require('node:fs');

const URL_ENV = 'AGENTHUB_MODEL_ENDPOINT_URL';
const MODEL_ENV = 'AGENTHUB_MODEL_NAME';
const KEY_ENV = 'AGENTHUB_MODEL_API_KEY';

// Claude Code refuses to start without some credential and asks for a login instead. A server
// without auth ignores the Authorization header, so any value works; this one says what it is when
// it turns up in a server log.
const KEYLESS_TOKEN = 'agenthub-no-key';

// How long each CLI waits for a self-hosted server's first byte. A server on one GPU processes the
// whole prompt before it sends headers, and a first turn carrying a CLI's system prompt and tool
// list took 4.5 minutes against llama-server with a 30B model; follow-up turns hit the prompt cache
// and answer in seconds. The CLIs' defaults are tuned for a provider's API: OpenCode gives up on
// headers after 5 minutes and Claude Code after its API timeout of 10 (both read from the pinned
// binaries); Codex's stream idle timeout is documented as 5 minutes. A CLI that gives up disconnects, the server drops the half-processed prompt, and the retry
// starts the same wait again, so the session never gets an answer.
const FIRST_TOKEN_TIMEOUT_MS = 30 * 60 * 1000;

// What a failed connection reports as its cause, as far as it helps someone fix the endpoint.
const CONNECT_ERRORS = Object.freeze([
  'ECONNREFUSED', 'ECONNRESET', 'ENOTFOUND', 'EAI_AGAIN', 'ETIMEDOUT', 'EHOSTUNREACH', 'ENETUNREACH',
  'UND_ERR_CONNECT_TIMEOUT', 'UND_ERR_SOCKET', 'TimeoutError', 'AbortError',
  'CERT_HAS_EXPIRED', 'DEPTH_ZERO_SELF_SIGNED_CERT', 'SELF_SIGNED_CERT_IN_CHAIN', 'UNABLE_TO_VERIFY_LEAF_SIGNATURE',
  'ERR_TLS_CERT_ALTNAME_INVALID'
]);

const PROTOCOLS = {
  messages: {
    agent: 'Claude Code',
    api: 'the Anthropic Messages API',
    path: base => claudeBaseUrl(base) + '/v1/messages',
    headers: key => ({ 'anthropic-version': '2023-06-01', ...(key ? { 'x-api-key': key } : {}) }),
    hint: 'A server that only offers OpenAI /v1/chat/completions cannot serve a Claude session.'
  },
  responses: {
    agent: 'Codex',
    api: 'the OpenAI Responses API',
    path: base => base + '/responses',
    headers: () => ({}),
    hint: 'Codex no longer supports /chat/completions; the server has to implement /responses.'
  },
  chat: {
    agent: 'OpenCode',
    api: 'OpenAI Chat Completions',
    path: base => base + '/chat/completions',
    headers: () => ({}),
    hint: 'Give the API base that /chat/completions hangs off, usually ending in /v1.'
  }
};

function settings(env = process.env) {
  const url = String(env[URL_ENV] || '').trim().replace(/\/+$/, '');
  const model = String(env[MODEL_ENV] || '').trim();
  const key = String(env[KEY_ENV] || '').trim();
  if (!url) throw new Error(URL_ENV + ' is not set; the session has no model endpoint.');
  if (!model) throw new Error(MODEL_ENV + ' is not set; the session has no model name.');
  return { url, model, key };
}

/**
 * Claude Code appends /v1/messages to ANTHROPIC_BASE_URL itself. Users give the OpenAI-style base,
 * which ends in /v1, because that is what Codex and OpenCode need; passed through unchanged it would
 * become /v1/v1/messages.
 */
function claudeBaseUrl(url) {
  return String(url).replace(/\/+$/, '').replace(/\/v1$/, '');
}

/**
 * The agent child's environment for Claude Code. Every model tier points at the one model: Claude
 * Code sends background work (titles, summaries) to a "haiku" tier by default, which a self-hosted
 * server does not have and would answer with an error the person never asked for.
 */
function claudeChildEnv(env = process.env) {
  const { url, model, key } = settings(env);
  return {
    ANTHROPIC_BASE_URL: claudeBaseUrl(url),
    ANTHROPIC_AUTH_TOKEN: key || KEYLESS_TOKEN,
    ANTHROPIC_MODEL: model,
    ANTHROPIC_DEFAULT_OPUS_MODEL: model,
    ANTHROPIC_DEFAULT_SONNET_MODEL: model,
    ANTHROPIC_DEFAULT_HAIKU_MODEL: model,
    CLAUDE_CODE_SUBAGENT_MODEL: model,
    API_TIMEOUT_MS: String(FIRST_TOKEN_TIMEOUT_MS),
    CLAUDE_STREAM_FIRST_BYTE_TIMEOUT_MS: String(FIRST_TOKEN_TIMEOUT_MS)
  };
}

function bounded(text, limit = 300) {
  const flat = String(text || '').replace(/\s+/g, ' ').trim();
  return flat.length > limit ? flat.slice(0, limit) + '…' : flat;
}

/**
 * Asks the server for the one route the agent will use, with a body that is deliberately invalid:
 * a server that has the route rejects it as a bad request without running the model, so the check
 * costs no inference even on a server that takes minutes for a first token. Only the answers that
 * mean "this will never work" fail; anything else, a 500 for the missing field included (that is
 * what llama-server answers on /v1/messages), lets the CLI start.
 */
async function probe({ url, model, key }, protocol, { fetchImpl = fetch, attempts = 3, delayMs = 5000, timeoutMs = 20000 } = {}) {
  const spec = PROTOCOLS[protocol];
  if (!spec) throw new Error('Unknown endpoint protocol: ' + protocol);
  const target = spec.path(url);
  const headers = {
    'content-type': 'application/json',
    ...(key ? { authorization: 'Bearer ' + key } : {}),
    ...spec.headers(key)
  };

  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt++) {
    let response;
    try {
      response = await fetchImpl(target, {
        method: 'POST', headers, body: JSON.stringify({ model }), signal: AbortSignal.timeout(timeoutMs)
      });
    } catch (error) {
      // A self-hosted server restarts more often than a provider's; a pod that started during a
      // restart should not fail the session for it.
      lastError = error;
      if (attempt < attempts) await new Promise(resolve => setTimeout(resolve, delayMs));
      continue;
    }
    const body = bounded(await response.text().catch(() => ''));
    // Only our own text reaches the termination message: the status is named by a literal per branch
    // rather than copied from the response, and the server's body goes to the log (detail).
    if (response.status === 401 || response.status === 403) {
      const code = response.status === 401 ? '401' : '403';
      return { ok: false, message: `The model endpoint refused the key (HTTP ${code}) at ${target}. ` +
        (key ? 'Check the stored custom endpoint API key.' : 'No custom endpoint API key is stored; add one in the credential settings.') };
    }
    if (response.status === 404 || response.status === 405) {
      // The termination message is shown in the session list to everyone who can read the session,
      // and a server's error body is not ours to show there.
      const code = response.status === 404 ? '404' : '405';
      return { ok: false, message: `${target} does not exist (HTTP ${code}): ${spec.agent} needs ${spec.api}. ${spec.hint}`,
        detail: body };
    }
    return { ok: true, status: response.status };
  }
  // Named from a fixed list, for the same reason as the status codes above.
  const raw = lastError && (lastError.cause?.code || lastError.name);
  const cause = CONNECT_ERRORS.find(known => known === raw);
  return { ok: false, message: `Cannot reach the model endpoint ${target} (${cause || 'no answer'}). ` +
    'If the server runs inside the cluster, the operator has to allow it in the chart (agent.modelEndpoints); ' +
    'see docs/custom-model-endpoints.md.' };
}

/** What the session list shows for a container that ended: docs/session-startup-failures.md. */
function writeTermination(message, file = process.env.TERMINATION_MESSAGE_PATH || '/dev/termination-log') {
  try { fs.writeFileSync(file, message.slice(0, 3500)); } catch { /* not in a pod */ }
}

async function main(argv) {
  const [command, protocol] = argv;
  if (command !== 'probe') throw new Error('usage: model-endpoint.js probe <messages|responses|chat>');
  const result = await probe(settings(), protocol);
  if (result.ok) {
    console.log(`[model-endpoint] ${PROTOCOLS[protocol].api} answered at ${PROTOCOLS[protocol].path(settings().url)} (HTTP ${result.status}).`);
    return 0;
  }
  console.error('[model-endpoint] ' + result.message);
  if (result.detail) console.error('[model-endpoint] Server said: ' + result.detail);
  writeTermination(result.message);
  return 1;
}

if (require.main === module) {
  main(process.argv.slice(2)).then(code => process.exit(code), error => {
    console.error('[model-endpoint] ' + error.message);
    writeTermination(error.message);
    process.exit(1);
  });
}

module.exports = { FIRST_TOKEN_TIMEOUT_MS, KEY_ENV, KEYLESS_TOKEN, PROTOCOLS, claudeBaseUrl, claudeChildEnv, probe, settings };
