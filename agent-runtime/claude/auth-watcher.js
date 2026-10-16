'use strict';

const fs = require('node:fs');
const path = require('node:path');

const { KnownHashes, baselineFile, sha256 } = require('../common/credential-install');
const { readIdentity, encodeIdentityHeader } = require('./account-identity');

const MAX_CREDENTIAL_BYTES = 64 * 1024;
// Bounds the final upload so a hung callback cannot hold the pod open past its grace period.
const SHUTDOWN_FLUSH_MS = 5_000;

function validCredential(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length === 0 || buffer.length > MAX_CREDENTIAL_BYTES) return false;
  try {
    const value = JSON.parse(buffer.toString('utf8'));
    return value !== null && !Array.isArray(value) && typeof value === 'object' &&
      value.claudeAiOauth !== null && !Array.isArray(value.claudeAiOauth) &&
      typeof value.claudeAiOauth === 'object';
  } catch {
    return false;
  }
}

function watchCredential(options) {
  const {
    source, callbackUrl, callbackToken, intervalMs = 30_000,
    fetchImpl = globalThis.fetch, logger = console,
    fsImpl = fs, setIntervalImpl = setInterval, clearIntervalImpl = clearInterval,
    unrefTimer = true, expectCreate = false, baselineHash, baselineFile: baselinePath,
    identitySource
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

  function identityHeaders() {
    if (!identitySource) return {};
    const header = encodeIdentityHeader(readIdentity(identitySource, fsImpl));
    return header ? { 'X-Agent-Identity': header } : {};
  }

  async function runPoll() {
    if (stopped) return;
    // A swap by the session agent counts as "already uploaded", and marks the watcher as past
    // its first read even if the file did not exist when it started.
    if (baselinePath && known.adopt(baselinePath, fsImpl)) initialized = true;
    let body;
    try {
      body = await readBounded();
    } catch (error) {
      if (error && error.code === 'ENOENT') {
        initialized = true;
        return;
      }
      logger.warn('[claude-auth] Credential file could not be checked.');
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
      const response = await fetchImpl(callbackUrl.replace(/\/$/, '') + '/claude-credentials', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'X-Agent-Token': callbackToken, ...identityHeaders() },
        body
      });
      if (!response || !response.ok) throw new Error('Credential upload rejected');
      known.add(hash);
      logger.info('[claude-auth] Credential backup updated.');
    } catch {
      logger.warn('[claude-auth] Credential backup failed; it will be retried.');
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
    /**
     * One last upload before the process goes away. Anthropic hands out a new refresh token
     * each time the old one is redeemed, so a rotation the CLI performed since the last poll
     * exists only in the pod's file. Losing it does not cost a session's worth of freshness:
     * the stored token has already been spent, so every later session starts from a refresh
     * token the server rejects, and the account has to be linked by hand again.
     */
    async flush() {
      if (stopped) return;
      await Promise.race([
        poll(),
        new Promise(resolve => setTimeout(resolve, SHUTDOWN_FLUSH_MS).unref?.())
      ]);
    },
    stop() {
      stopped = true;
      clearIntervalImpl(timer);
    }
  };
}

if (require.main === module) {
  const home = process.env.HOME || '';
  const watcher = watchCredential({
    source: path.join(home, '.claude', '.credentials.json'),
    identitySource: path.join(home, '.claude.json'),
    baselineFile: baselineFile(process.env),
    callbackUrl: process.env.AGENTHUB_CALLBACK_URL,
    callbackToken: process.env.AGENTHUB_CALLBACK_TOKEN,
    expectCreate: process.env.AGENTHUB_CLAUDE_AUTH_EXPECT_CREATE === '1',
    baselineHash: process.env.AGENTHUB_CLAUDE_AUTH_BASELINE_SHA256,
    unrefTimer: false
  });
  for (const signal of ['SIGTERM', 'SIGINT']) {
    process.once(signal, () => {
      watcher.flush().finally(() => {
        watcher.stop();
        process.exit(0);
      });
    });
  }
}

module.exports = { watchCredential, validCredential, MAX_CREDENTIAL_BYTES, SHUTDOWN_FLUSH_MS };
