'use strict';

const fs = require('node:fs');

const { KnownHashes, baselineFile, sha256 } = require('../common/credential-install');

const MAX_CREDENTIAL_BYTES = 64 * 1024;
const ENTRY_TYPES = new Set(['api', 'oauth', 'wellknown']);

// Shape pinned from OpenCode 1.18.34 (packages/opencode/src/auth): a map keyed by provider id,
// each entry { type: "api" | "oauth" | "wellknown", ... }. Mirrors ProviderCredentialValidator,
// so the watcher never uploads what the backend would reject. An empty map is what a logout
// leaves behind; uploading it would replace a working stored login with nothing.
function validCredential(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length === 0 || buffer.length > MAX_CREDENTIAL_BYTES) return false;
  try {
    const value = JSON.parse(buffer.toString('utf8'));
    if (value === null || Array.isArray(value) || typeof value !== 'object') return false;
    const entries = Object.values(value);
    return entries.length > 0 && entries.every(entry =>
      entry !== null && typeof entry === 'object' && !Array.isArray(entry) && ENTRY_TYPES.has(entry.type));
  } catch {
    return false;
  }
}

function watchCredential(options) {
  const {
    source, callbackUrl, callbackToken, intervalMs = 30_000,
    fetchImpl = globalThis.fetch, logger = console,
    fsImpl = fs, setIntervalImpl = setInterval, clearIntervalImpl = clearInterval,
    unrefTimer = true, expectCreate = false, baselineHash, baselineFile: baselinePath
  } = options || {};
  if (!source || !callbackUrl || !callbackToken || typeof fetchImpl !== 'function') {
    throw new Error('Credential watcher requires source, callback URL, callback token, and fetch');
  }

  const hasBaseline = typeof baselineHash === 'string' && /^[a-f0-9]{64}$/.test(baselineHash);
  let initialized = hasBaseline;
  // Everything in here has either been uploaded or was installed by the hub itself (the
  // entrypoint's restore, or a swap by the session agent) and must not be echoed back.
  const known = new KnownHashes(hasBaseline ? baselineHash : undefined);
  let stopped = false;
  let active = Promise.resolve();

  async function readBounded() {
    let handle;
    try {
      handle = await fsImpl.promises.open(source, 'r');
      const stat = await handle.stat();
      if (!stat.isFile() || stat.size > MAX_CREDENTIAL_BYTES) return null;
      const body = await handle.readFile();
      return body.length <= MAX_CREDENTIAL_BYTES ? body : null;
    } finally {
      if (handle) await handle.close();
    }
  }

  async function runPoll() {
    if (stopped) return;
    if (baselinePath && known.adopt(baselinePath, fsImpl)) initialized = true;
    let body;
    try {
      body = await readBounded();
    } catch (error) {
      if (error && error.code === 'ENOENT') {
        initialized = true;
        return;
      }
      logger.warn('[opencode-auth] Credential file could not be checked.');
      return;
    }
    if (!body || !validCredential(body)) {
      initialized = true;
      return;
    }

    const hash = sha256(body);
    if (!initialized) {
      initialized = true;
      if (!expectCreate) {
        known.add(hash);
        return;
      }
    }
    if (known.has(hash)) return;

    try {
      const response = await fetchImpl(callbackUrl.replace(/\/$/, '') + '/opencode-credentials', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'X-Agent-Token': callbackToken },
        body
      });
      if (!response || !response.ok) throw new Error('Credential upload rejected');
      known.add(hash);
      logger.info('[opencode-auth] Credential backup updated.');
    } catch {
      logger.warn('[opencode-auth] Credential backup failed; it will be retried.');
    }
  }

  function poll() {
    active = active.then(runPoll, runPoll);
    return active;
  }

  const ready = poll();
  const timer = setIntervalImpl(poll, intervalMs);
  if (unrefTimer && timer && typeof timer.unref === 'function') timer.unref();
  return {
    ready,
    poll,
    stop() {
      stopped = true;
      clearIntervalImpl(timer);
    }
  };
}

if (require.main === module) {
  const watcher = watchCredential({
    source: process.env.OPENCODE_AUTH_FILE,
    baselineFile: baselineFile(process.env),
    callbackUrl: process.env.AGENTHUB_CALLBACK_URL,
    callbackToken: process.env.AGENTHUB_CALLBACK_TOKEN,
    expectCreate: process.env.AGENTHUB_OPENCODE_AUTH_EXPECT_CREATE === '1',
    baselineHash: process.env.AGENTHUB_OPENCODE_AUTH_BASELINE_SHA256,
    unrefTimer: false
  });
  for (const signal of ['SIGTERM', 'SIGINT']) {
    process.once(signal, () => {
      watcher.stop();
      process.exit(0);
    });
  }
}

module.exports = { watchCredential, validCredential, MAX_CREDENTIAL_BYTES };
