'use strict';

// The models the session's account offers, asked from the pinned CLI itself (#461,
// docs/design/model-discovery.md). The probe runs as a second, short-lived CLI process with the
// agent's own HOME, environment and login, so the answer is the one this account gets — not a
// list the hub keeps, and not one fetched with a credential the hub holds. The interactive CLI is
// never touched.
//
// Everything that leaves this module is rebuilt from a fixed set of fields: the CLIs' raw answers
// carry the account's e-mail and organisation (Claude), provider URLs and headers (OpenCode), and an
// error text can quote whatever the service sent back.

const crypto = require('node:crypto');
const childProcess = require('node:child_process');

const MAX_MODELS = 200;
const MAX_TEXT = 300;
const MAX_OUTPUT_BYTES = 8 * 1024 * 1024;
const PROBE_TIMEOUT_MS = 60_000;
const CACHE_MS = 10 * 60 * 1000;
// Ids as the CLIs print them: "claude-fable-5-1[1m]", "opencode-go/glm-5.3", "gpt-6-luna".
// Nothing a shell or a TOML string would read differently, and no spaces.
const MODEL_ID = /^[A-Za-z0-9][A-Za-z0-9._:/@[\]+=,-]{0,199}$/;
const EFFORT = /^[a-z][a-z0-9_-]{0,23}$/;

// Environment variables that hold a provider credential in some runtime. Their values are masked
// in any error text and feed the credential fingerprint.
const CREDENTIAL_ENV = ['ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'CODEX_API_KEY', 'OPENAI_API_KEY',
  'CURSOR_API_KEY', 'CURSOR_AUTH_TOKEN', 'OPENCODE_API_KEY', 'AGENTHUB_MODEL_API_KEY'];
// Token shapes a service may echo back although they are not in the environment: a refreshed
// access token, a key quoted in a 401 body.
const TOKEN_SHAPES = [
  /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]*)?/g,
  /\b(?:sk|rk|pk|key|crsr|sess)[-_][A-Za-z0-9_-]{8,}/gi,
  /\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}/gi
];

function clip(value, max = MAX_TEXT) {
  if (typeof value !== 'string') return undefined;
  const text = value.replace(/\x1b\[[0-9;?]*[ -/]*[@-~]/g, '').replace(/[\x00-\x1f\x7f]+/g, ' ').trim();
  if (!text) return undefined;
  return text.length > max ? text.slice(0, max - 1) + '…' : text;
}

/** Masks credential values and token-shaped strings; returns a bounded single line. */
function redact(text, env = {}, mask) {
  let out = typeof text === 'string' ? text : String(text ?? '');
  for (const name of CREDENTIAL_ENV) {
    const value = env[name];
    if (typeof value === 'string' && value.length >= 6) out = out.split(value).join('[redacted]');
  }
  for (const shape of TOKEN_SHAPES) out = out.replace(shape, m => /^(Bearer|Basic)\s/i.test(m) ? m.split(/\s+/)[0] + ' [redacted]' : '[redacted]');
  if (typeof mask === 'function') {
    try { out = mask(out); } catch {}
  }
  return clip(out) || '';
}

function positiveInt(value) {
  return Number.isSafeInteger(value) && value > 0 ? value : undefined;
}

/** One model as every probe reports it; anything not listed here is dropped. */
function normaliseModel(raw) {
  if (!raw || typeof raw !== 'object' || typeof raw.id !== 'string' || !MODEL_ID.test(raw.id)) return null;
  const caps = raw.capabilities && typeof raw.capabilities === 'object' ? raw.capabilities : {};
  const efforts = Array.isArray(caps.reasoningEfforts)
    ? [...new Set(caps.reasoningEfforts.filter(e => typeof e === 'string' && EFFORT.test(e)))].slice(0, 12) : undefined;
  const capabilities = {};
  if (efforts && efforts.length) capabilities.reasoningEfforts = efforts;
  if (typeof caps.defaultEffort === 'string' && EFFORT.test(caps.defaultEffort)) capabilities.defaultEffort = caps.defaultEffort;
  for (const flag of ['imageInput', 'reasoning', 'toolCall', 'fastMode', 'adaptiveThinking']) {
    if (typeof caps[flag] === 'boolean') capabilities[flag] = caps[flag];
  }
  const contextWindow = positiveInt(caps.contextWindow);
  if (contextWindow) capabilities.contextWindow = contextWindow;
  const model = { id: raw.id };
  const label = clip(raw.label, 120);
  if (label) model.label = label;
  const description = clip(raw.description);
  if (description) model.description = description;
  if (typeof raw.resolvedId === 'string' && MODEL_ID.test(raw.resolvedId) && raw.resolvedId !== raw.id)
    model.resolvedId = raw.resolvedId;
  if (raw.isDefault === true) model.isDefault = true;
  if (Object.keys(capabilities).length) model.capabilities = capabilities;
  return model;
}

function normaliseModels(list) {
  const seen = new Set();
  const models = [];
  for (const raw of Array.isArray(list) ? list : []) {
    const model = normaliseModel(raw);
    if (!model || seen.has(model.id)) continue;
    seen.add(model.id);
    models.push(model);
    if (models.length >= MAX_MODELS) break;
  }
  return models;
}

/**
 * Runs a CLI with bounded output and time. `onStdout(text)` may return true to end the process
 * early, once the answer is complete: Claude's and Codex's servers otherwise keep running.
 */
function runCli({ cmd, args = [], env, cwd, input, timeoutMs = PROBE_TIMEOUT_MS, onStdout, spawn = childProcess.spawn }) {
  return new Promise(resolve => {
    let stdout = '';
    let stderr = '';
    let done = false;
    let timedOut = false;
    let child;
    const finish = (code, error) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      resolve({ stdout, stderr, code, timedOut, error });
    };
    try {
      child = spawn(cmd, args, { env, cwd, stdio: ['pipe', 'pipe', 'pipe'] });
    } catch (error) {
      return resolve({ stdout, stderr, code: null, timedOut, error });
    }
    const stop = () => { try { child.kill('SIGTERM'); } catch {} };
    const timer = setTimeout(() => { timedOut = true; stop(); finish(null); }, timeoutMs);
    child.on('error', error => finish(null, error));
    child.on('close', code => finish(code));
    child.stdout.on('data', chunk => {
      if (stdout.length < MAX_OUTPUT_BYTES) stdout += chunk;
      if (onStdout && onStdout(stdout)) { stop(); finish(0); }
    });
    child.stderr.on('data', chunk => { if (stderr.length < 256 * 1024) stderr += chunk; });
    child.stdin.on('error', () => {});
    if (input !== undefined) child.stdin.write(input);
  });
}

/**
 * A digest of the credential material the CLI reads. An account switch rewrites it, so a cached
 * answer for the previous account is not served. Never logged and never returned.
 */
function credentialFingerprint({ env = {}, files = [], fs = require('node:fs') }) {
  const hash = crypto.createHash('sha256');
  hash.update(String(env.AGENTHUB_AUTH_MODE || ''));
  hash.update('\0' + String(env.AGENTHUB_MODEL_ENDPOINT_URL || ''));
  for (const name of CREDENTIAL_ENV) hash.update('\0' + name + '=' + String(env[name] || ''));
  for (const file of files) {
    hash.update('\0' + file + ':');
    try { hash.update(fs.readFileSync(file)); } catch { hash.update('-'); }
  }
  return hash.digest('hex');
}

/** The OpenAI-shaped `GET <base>/models` of a custom endpoint, from inside the pod. */
async function probeEndpoint({ env, fetch: fetchImpl = fetch, timeoutMs = 20_000 }) {
  const base = String(env.AGENTHUB_MODEL_ENDPOINT_URL || '').trim().replace(/\/+$/, '');
  const key = String(env.AGENTHUB_MODEL_API_KEY || '').trim();
  if (!base) return { source: 'endpoint', state: 'error', error: { code: 'not_configured', message: 'The session has no model endpoint.' } };
  const headers = { accept: 'application/json' };
  if (key) { headers.authorization = 'Bearer ' + key; headers['x-api-key'] = key; }
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetchImpl(base + '/models', { headers, signal: controller.signal, redirect: 'manual' });
    const text = await response.text();
    if (response.status === 401 || response.status === 403) {
      return { source: 'endpoint', verified: true, state: 'error', error: { code: 'not_entitled',
        message: `The endpoint refused to list models (HTTP ${response.status}): ${text}` } };
    }
    if (!response.ok) {
      return { source: 'endpoint', verified: true, state: 'error', error: { code: 'endpoint_error',
        message: `The endpoint answered HTTP ${response.status} to GET /models.` } };
    }
    let body;
    try { body = JSON.parse(text); } catch {
      return { source: 'endpoint', verified: true, state: 'error', error: { code: 'unparseable', message: 'The endpoint\'s /models answer is not JSON.' } };
    }
    const rows = Array.isArray(body && body.data) ? body.data : Array.isArray(body && body.models) ? body.models : [];
    const configured = String(env.AGENTHUB_MODEL_NAME || '');
    return { source: 'endpoint', verified: true, state: 'ok', models: rows.map(row => ({
      id: row && (row.id || row.name || row.model),
      label: row && (row.display_name || row.name),
      isDefault: row && (row.id || row.name || row.model) === configured,
      capabilities: { contextWindow: row && (row.context_length || row.max_context_length || (row.meta && row.meta.n_ctx_train)) }
    })) };
  } catch (error) {
    return { source: 'endpoint', state: 'error', error: { code: 'unreachable',
      message: error && error.name === 'AbortError' ? 'The endpoint did not answer GET /models in time.' : 'The endpoint could not be reached: ' + (error && error.message) } };
  } finally {
    clearTimeout(timer);
  }
}

/**
 * The catalog service of one session agent. `driver.modelCatalog` is `{ probe(ctx) }` from the
 * runtime; a custom endpoint is probed the same way for every runtime that supports one.
 */
function createModelCatalog({ driver, env, home, cwd, fs = require('node:fs'), fetch: fetchImpl = fetch,
  spawn = childProcess.spawn, now = Date.now, secretMask, cacheMs = CACHE_MS } = {}) {
  let cached = null;
  let inflight = null;
  const support = driver && driver.modelCatalog;
  const endpointMode = () => env.AGENTHUB_AUTH_MODE === 'customendpoint';
  const ctx = () => ({ env, home, cwd, fs, fetch: fetchImpl,
    run: options => runCli({ env, cwd: home, spawn, ...options }) });

  // The login file the credential switch rewrites (PUT /agenthub/credentials), wherever the
  // runtime keeps it.
  function fingerprint() {
    let file = null;
    try {
      file = typeof driver.credentialPath === 'function' ? driver.credentialPath(env)
        : require('node:path').join(home, driver.stateDir, driver.authFilename);
    } catch {}
    return credentialFingerprint({ env, files: file ? [file] : [], fs });
  }

  async function probe() {
    let result;
    try {
      if (endpointMode()) result = await probeEndpoint({ env, fetch: fetchImpl });
      else if (!support) result = { source: 'none', state: 'unsupported' };
      else result = await support.probe(ctx());
    } catch (error) {
      result = { source: 'cli', state: 'error', error: { code: 'probe_failed', message: String(error && error.message || error) } };
    }
    const out = {
      agent: driver && driver.name,
      source: result.source || 'cli',
      verified: result.verified === true,
      state: ['ok', 'fallback', 'error', 'unsupported'].includes(result.state) ? result.state : 'error',
      models: normaliseModels(result.models),
      fetchedAt: new Date(now()).toISOString()
    };
    const version = clip(result.cliVersion, 60);
    if (version) out.cliVersion = version;
    if (result.error) {
      out.error = { code: /^[a-z_]{1,40}$/.test(result.error.code || '') ? result.error.code : 'probe_failed',
        message: redact(result.error.message, env, secretMask) };
    }
    if (out.state === 'ok' && out.models.length === 0 && !result.allowEmpty) {
      out.state = 'error';
      out.error = out.error || { code: 'empty', message: 'The CLI listed no models for this account.' };
    }
    return out;
  }

  /** The catalog, from the cache unless `refresh` or the credential changed since it was taken. */
  async function get({ refresh = false } = {}) {
    const key = fingerprint();
    if (!refresh && cached && cached.key === key && now() - cached.at < cacheMs) return cached.value;
    if (inflight && inflight.key === key) return inflight.promise;
    const promise = probe().then(value => {
      // A failed probe is not cached: the next request may come after the person fixed the login.
      if (value.state === 'ok' || value.state === 'fallback') cached = { key, at: now(), value };
      return value;
    }).finally(() => { if (inflight && inflight.promise === promise) inflight = null; });
    inflight = { key, promise };
    return promise;
  }

  return { get, supported: () => endpointMode() || Boolean(support) };
}

module.exports = { CACHE_MS, MAX_MODELS, MODEL_ID, clip, createModelCatalog, credentialFingerprint, normaliseModel,
  normaliseModels, probeEndpoint, redact, runCli };
