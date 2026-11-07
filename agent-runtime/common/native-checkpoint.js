'use strict';
const path = require('node:path');
const FILE = 'agenthub-native-checkpoint.json';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function read(directory, fs) {
  let raw;
  try { raw = fs.readFileSync(path.join(directory, FILE), 'utf8'); }
  catch (error) { if (error.code === 'ENOENT') return null; throw new Error('Saved native checkpoint metadata is unavailable.'); }
  let value;
  try { value = raw.length <= 2048 && JSON.parse(raw); } catch {}
  if (!value || value.version !== 1 || !UUID.test(value.baseNativeId || '') || !UUID.test(value.nativeId || ''))
    throw new Error('Saved native checkpoint metadata is invalid.');
  return value;
}

function resolve(env, directory, fs, legacyId) {
  if (env.AGENTHUB_REQUIRE_NATIVE_RESUME !== '1' || env.AGENTHUB_STATE_RESTORED !== '1') return null;
  const expected = env.AGENTHUB_NATIVE_SESSION_ID;
  if (!UUID.test(expected || '')) throw new Error('The committed native conversation identity is unavailable.');
  const saved = read(directory, fs);
  if (saved) {
    if (saved.baseNativeId !== expected && saved.nativeId !== expected)
      throw new Error('Saved native checkpoint does not match the committed conversation.');
    return saved.nativeId;
  }
  // Older archives have no checkpoint. Their native pointer can reflect an explicit clear;
  // replacing it with the original registry ID would restore the context the owner cleared.
  if (!UUID.test(legacyId || '')) throw new Error('The saved native conversation pointer is unavailable.');
  return legacyId;
}

function save(env, directory, fs, nativeId) {
  if (!UUID.test(nativeId || '')) return;
  const prior = read(directory, fs);
  const base = prior?.baseNativeId || (env.AGENTHUB_REQUIRE_NATIVE_RESUME === '1' && UUID.test(env.AGENTHUB_NATIVE_SESSION_ID || '')
    ? env.AGENTHUB_NATIVE_SESSION_ID : nativeId);
  if (prior?.nativeId === nativeId) return;
  const file = path.join(directory, FILE);
  fs.writeFileSync(file + '.tmp', JSON.stringify({ version: 1, baseNativeId: base, nativeId }), { mode: 0o600 });
  fs.renameSync(file + '.tmp', file);
}
module.exports = { FILE, read, resolve, save };
