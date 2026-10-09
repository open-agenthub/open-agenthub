'use strict';

const fs = require('node:fs');

const MAX_FIELD = 200;

function text(value) {
  if (typeof value !== 'string') return null;
  const cleaned = Array.from(value.trim()).filter(ch => ch.charCodeAt(0) >= 0x20 && ch !== '\u007f').join('');
  if (!cleaned) return null;
  return cleaned.length > MAX_FIELD ? cleaned.slice(0, MAX_FIELD) : cleaned;
}

/**
 * Who the Claude login belongs to, read from ~/.claude.json. The credential file itself holds
 * opaque tokens only; the CLI writes `oauthAccount` next to it at login (field names confirmed
 * against the 2.1.285 binary: accountUuid, emailAddress, organizationUuid, organizationName).
 * Display only on the hub — see docs/provider-accounts.md — so a missing or odd file is simply
 * "no identity", never a failure of the upload it travels with.
 */
function readIdentity(configPath, fsImpl = fs) {
  let config;
  try {
    config = JSON.parse(fsImpl.readFileSync(configPath, 'utf8'));
  } catch {
    return null;
  }
  const account = config && typeof config === 'object' ? config.oauthAccount : null;
  if (!account || typeof account !== 'object' || Array.isArray(account)) return null;
  // The same person in two organisations is two accounts, which is the case the feature exists
  // for; hence both uuids in the matching key.
  const key = [text(account.accountUuid), text(account.organizationUuid)].filter(Boolean).join(':') || null;
  const identity = { key, email: text(account.emailAddress), organization: text(account.organizationName) };
  return identity.key || identity.email || identity.organization ? identity : null;
}

/** base64url JSON — an organisation name is not guaranteed to be header-safe ASCII. */
function encodeIdentityHeader(identity) {
  if (!identity) return null;
  return Buffer.from(JSON.stringify(identity), 'utf8').toString('base64url');
}

module.exports = { readIdentity, encodeIdentityHeader };
