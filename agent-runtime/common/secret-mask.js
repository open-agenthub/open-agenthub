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

// Collect values rather than credential-looking keys: custom headers and env names are valid.
function mcpCredentials(config, add, credential = false) {
  if (Array.isArray(config)) {
    for (const item of config) mcpCredentials(item, add, credential);
  } else if (config && typeof config === 'object') {
    for (const [name, value] of Object.entries(config)) {
      const key = name.toLowerCase();
      mcpCredentials(value, add, credential || ['headers', 'env', 'url'].includes(key));
      if (key === 'url' && typeof value === 'string') {
        try {
          const url = new URL(value);
          for (const pair of url.search.slice(1).split('&')) {
            const equals = pair.indexOf('=');
            if (equals < 0) continue;
            const raw = pair.slice(equals + 1);
            add(raw);
            try { add(decodeURIComponent(raw.replace(/\+/g, ' '))); } catch {}
          }
          for (const part of [url.username, url.password]) {
            add(part);
            try { add(decodeURIComponent(part)); } catch {}
          }
        } catch {}
      }
    }
  } else if (credential && typeof config === 'string') {
    add(config);
    // Authorization schemes are often omitted when an agent prints just the token.
    const space = config.lastIndexOf(' ');
    if (space > 0) add(config.slice(space + 1));
  }
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
    // OpenClaw's MCP values, moved out of its archived config into the environment (#446). The
    // same values are in AGENTHUB_MCP_CONFIG below; named here too so masking does not depend on
    // that file still being there.
    for (const [name, value] of Object.entries(env)) {
      if (name.startsWith('AGENTHUB_MCP_VALUE_') && typeof value === 'string') values.push([name, value]);
    }
    if (typeof env.AGENTHUB_CALLBACK_TOKEN === 'string')
      values.push(['AGENTHUB_CALLBACK_TOKEN', env.AGENTHUB_CALLBACK_TOKEN]);
    // The entrypoint exports the effective config path; reread projected config on refresh too.
    for (const file of new Set([env.AGENTHUB_MCP_CONFIG].filter(Boolean))) {
      try {
        mcpCredentials(JSON.parse(fs.readFileSync(file, 'utf8')), value => values.push(['MCP', value]));
      } catch {}
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
