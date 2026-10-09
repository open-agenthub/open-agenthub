'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const MAX_CREDENTIAL_BYTES = 64 * 1024;
// Bounded: a session lives hours and a credential rotates a handful of times in that span, so
// this never fills up in practice; the cap only keeps a pathological loop from growing it.
const MAX_KNOWN_HASHES = 64;

function sha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

function isHash(value) {
  return typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
}

/**
 * Where the session agent tells the credential watcher about a file it installed. The watcher
 * is a separate process started by the entrypoint, so the only channel between them that needs
 * no PID and no socket is a file; HOME is the one writable place every runtime shares.
 */
function baselineFile(env = process.env) {
  return path.join(env.HOME || '/home/agent', '.agenthub', 'credential-baseline');
}

function readBaselineHash(file, fsImpl = fs) {
  try {
    const value = fsImpl.readFileSync(file, 'utf8').trim();
    return isHash(value) ? value : null;
  } catch {
    return null;
  }
}

function writeBaselineHash(file, hash, fsImpl = fs) {
  if (!isHash(hash)) throw new Error('A baseline must be a sha256 hex digest');
  fsImpl.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
  fsImpl.writeFileSync(file, hash + '\n', { mode: 0o600 });
}

/**
 * The hashes a watcher treats as already uploaded. A set rather than the single "last uploaded"
 * value the watchers started with: a poll can land between the agent writing the baseline and
 * writing the credential, and with one value the baseline would be the new hash while the file
 * still carries the old one — which the watcher would then upload as a change.
 */
class KnownHashes {
  constructor(initial) {
    this.values = new Set();
    if (isHash(initial)) this.values.add(initial);
  }

  has(hash) { return this.values.has(hash); }

  add(hash) {
    if (!isHash(hash)) return;
    this.values.add(hash);
    while (this.values.size > MAX_KNOWN_HASHES) {
      const oldest = this.values.values().next().value;
      this.values.delete(oldest);
    }
  }

  /** Adopts the baseline the agent wrote, if any. Returns true when it was new. */
  adopt(file, fsImpl = fs) {
    const hash = readBaselineHash(file, fsImpl);
    if (!hash || this.values.has(hash)) return false;
    this.add(hash);
    return true;
  }
}

/**
 * Writes a credential file the way every entrypoint does — 0600, directory 0700 — and
 * atomically, so a watcher poll or the CLI itself never reads a half-written file.
 */
function writeCredentialFile(target, body, fsImpl = fs) {
  const directory = path.dirname(target);
  fsImpl.mkdirSync(directory, { recursive: true, mode: 0o700 });
  const temporary = path.join(directory, '.' + path.basename(target) + '.' + process.pid + '.tmp');
  fsImpl.writeFileSync(temporary, body, { mode: 0o600 });
  fsImpl.renameSync(temporary, target);
}

/**
 * The one path a provider file may be written to: the driver's own, or the default every
 * entrypoint restores the secret into, and in either case strictly under HOME. The request
 * carries no path at all; this is where "no arbitrary paths" is enforced.
 */
function credentialTarget(env, driver) {
  const home = path.resolve(env.HOME || '/home/agent');
  const custom = typeof driver.credentialPath === 'function' ? driver.credentialPath(env) : null;
  const target = path.resolve(custom || path.join(home, driver.stateDir, driver.authFilename));
  if (target !== home && !target.startsWith(home + path.sep)) {
    throw new Error('Credential path escapes the agent home directory');
  }
  return target;
}

function looksLikeJsonObject(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length === 0 || buffer.length > MAX_CREDENTIAL_BYTES) return false;
  try {
    const value = JSON.parse(buffer.toString('utf8'));
    return value !== null && typeof value === 'object' && !Array.isArray(value);
  } catch {
    return false;
  }
}

module.exports = {
  MAX_CREDENTIAL_BYTES, MAX_KNOWN_HASHES, sha256, isHash, baselineFile, readBaselineHash, writeBaselineHash,
  KnownHashes, writeCredentialFile, credentialTarget, looksLikeJsonObject
};
