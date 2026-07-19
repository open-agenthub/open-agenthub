'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');

const MAX_CREDENTIAL_BYTES = 64 * 1024;

function validCredential(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length === 0 || buffer.length > MAX_CREDENTIAL_BYTES) return false;
  try {
    const value = JSON.parse(buffer.toString('utf8'));
    return value !== null && !Array.isArray(value) && typeof value === 'object' &&
      value.tokens !== null && !Array.isArray(value.tokens) && typeof value.tokens === 'object';
  } catch {
    return false;
  }
}

function watchCredential(options) {
  const {
    source, callbackUrl, callbackToken, intervalMs = 30_000,
    fetchImpl = globalThis.fetch, logger = console,
    fsImpl = fs, setIntervalImpl = setInterval, clearIntervalImpl = clearInterval,
    unrefTimer = true
  } = options || {};
  if (!source || !callbackUrl || !callbackToken || typeof fetchImpl !== 'function') {
    throw new Error('Credential watcher requires source, callback URL, callback token, and fetch');
  }

  let initialized = false;
  let lastUploadedHash;
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
    let body;
    try {
      body = await readBounded();
    } catch (error) {
      if (error && error.code === 'ENOENT') {
        initialized = true;
        return;
      }
      logger.warn('[codex-auth] Credential file could not be checked.');
      return;
    }
    if (!body || !validCredential(body)) {
      initialized = true;
      return;
    }

    const hash = crypto.createHash('sha256').update(body).digest('hex');
    if (!initialized) {
      initialized = true;
      lastUploadedHash = hash;
      return;
    }
    if (hash === lastUploadedHash) return;

    try {
      const response = await fetchImpl(callbackUrl.replace(/\/$/, '') + '/codex-credentials', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'X-Agent-Token': callbackToken },
        body
      });
      if (!response || !response.ok) throw new Error('Credential upload rejected');
      lastUploadedHash = hash;
      logger.info('[codex-auth] Credential backup updated.');
    } catch {
      logger.warn('[codex-auth] Credential backup failed; it will be retried.');
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
    source: process.env.CODEX_HOME + '/auth.json',
    callbackUrl: process.env.AGENTHUB_CALLBACK_URL,
    callbackToken: process.env.AGENTHUB_CALLBACK_TOKEN,
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
