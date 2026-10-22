'use strict';

// Masks the values of the session's secrets (docs/session-secrets.md) in what the session agent
// returns for other people to read: scrollback replay, uploads and live provider transcripts. The same rules as
// the backend's SecretMask, which covers the hook messages the chat relays post.
//
// What the live terminal shows is not masked: the person watching it is the owner or someone the
// owner gave terminal access, and either can read the files in ~/.secrets anyway.

const path = require('node:path');

// Shorter values are left alone: "1234" or "true" would be replaced in every number and sentence,
// which mangles the text and still gives the value away by where the gaps are.
const MIN_LENGTH = 8;

function needles(value) {
  const candidates = [value, value.replace(/[\r\n]+$/, ''), JSON.stringify(value).slice(1, -1)];
  // A terminal turns \n into \r\n, and an agent often prints only part of a key file.
  for (const line of value.split('\n')) candidates.push(line.replace(/\r$/, ''));
  return candidates.filter(c => c.trim().length >= MIN_LENGTH);
}

function createSecretMask({ env = process.env, fs = require('node:fs') } = {}) {
  let patterns = [];

  // Read again before every upload, because the kubelet replaces the files of a running pod when
  // the owner changes a secret, and a value added since the last tick must not slip through.
  function refresh() {
    const values = [];
    const dir = env.AGENTHUB_SECRETS_DIR || '';
    if (dir) {
      let names = [];
      try { names = fs.readdirSync(dir); } catch {}
      for (const name of names) {
        // The kubelet's own "..data" link and timestamped directories live next to the files.
        if (name.startsWith('.')) continue;
        try { values.push([name, fs.readFileSync(path.join(dir, name), 'utf8')]); } catch {}
      }
    }
    for (const name of String(env.AGENTHUB_SECRET_ENV || '').split(',').map(n => n.trim()).filter(Boolean)) {
      if (typeof env[name] === 'string') values.push([name, env[name]]);
    }
    const seen = new Set();
    patterns = [];
    for (const [name, value] of values) {
      for (const needle of needles(value)) {
        if (seen.has(needle)) continue;
        seen.add(needle);
        patterns.push([needle, '[secret:' + name + ']']);
      }
    }
    // Longest first, so a value containing another is replaced whole.
    patterns.sort((a, b) => b[0].length - a[0].length);
    return patterns.length;
  }

  function mask(text) {
    if (typeof text !== 'string' || !text || patterns.length === 0) return text;
    for (const [needle, replacement] of patterns) text = text.split(needle).join(replacement);
    return text;
  }

  return { refresh, mask, active: () => patterns.length > 0 };
}

module.exports = { createSecretMask, MIN_LENGTH };
