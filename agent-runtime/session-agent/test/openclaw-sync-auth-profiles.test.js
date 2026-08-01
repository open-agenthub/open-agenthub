'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  exportFromSqlite, importToSqlite, readStoreFromSqlite, writeStoreToSqlite, validStore
} = require('../../openclaw/sync-auth-profiles');

function store(token) {
  return {
    version: 1,
    profiles: {
      'anthropic:default': {
        type: 'api_key',
        provider: 'anthropic',
        key: 'synthetic-key-' + token
      }
    }
  };
}

function tempEnv() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'openclaw-sync-'));
  const stateDir = path.join(root, '.openclaw');
  const agentDir = path.join(stateDir, 'agents', 'main', 'agent');
  fs.mkdirSync(agentDir, { recursive: true });
  return {
    root,
    HOME: root,
    OPENCLAW_STATE_DIR: stateDir,
    OPENCLAW_AGENT_DIR: agentDir,
    OPENCLAW_AUTH_FILE: path.join(stateDir, 'auth-profiles.json'),
    AGENTHUB_OPENCLAW_AGENT_ID: 'main'
  };
}

test('OpenClaw sync rejects empty or placeholder credential shapes', () => {
  assert.equal(validStore({ version: 1, profiles: {} }), false);
  assert.equal(validStore({ openclawAuth: { accessToken: 'x' } }), false);
  assert.equal(validStore(store('ok')), true);
});

test('OpenClaw sync imports Secret JSON into agents/main/agent SQLite and exports back', () => {
  const env = tempEnv();
  const payload = store('import-roundtrip');
  fs.writeFileSync(env.OPENCLAW_AUTH_FILE, JSON.stringify(payload));

  assert.equal(importToSqlite(env), true);
  const sqlitePath = path.join(env.OPENCLAW_AGENT_DIR, 'openclaw-agent.sqlite');
  assert.equal(fs.existsSync(sqlitePath), true);
  assert.deepEqual(readStoreFromSqlite(sqlitePath).profiles['anthropic:default'].key,
    'synthetic-key-import-roundtrip');
  assert.equal(fs.existsSync(path.join(env.OPENCLAW_AGENT_DIR, 'auth-profiles.json')), true);

  fs.unlinkSync(env.OPENCLAW_AUTH_FILE);
  assert.equal(exportFromSqlite(env), true);
  const exported = JSON.parse(fs.readFileSync(env.OPENCLAW_AUTH_FILE, 'utf8'));
  assert.deepEqual(exported.profiles['anthropic:default'].key, 'synthetic-key-import-roundtrip');
});

test('OpenClaw sync export is a no-op when SQLite is missing', () => {
  const env = tempEnv();
  assert.equal(exportFromSqlite(env), false);
  assert.equal(fs.existsSync(env.OPENCLAW_AUTH_FILE), false);
});

test('OpenClaw sync writeStoreToSqlite round-trips store_json', () => {
  const env = tempEnv();
  const sqlitePath = path.join(env.OPENCLAW_AGENT_DIR, 'openclaw-agent.sqlite');
  writeStoreToSqlite(sqlitePath, store('direct'));
  assert.deepEqual(readStoreFromSqlite(sqlitePath).profiles['anthropic:default'].key,
    'synthetic-key-direct');
});

test('OpenClaw sync export after import leaves a stable auth-file hash', () => {
  const crypto = require('node:crypto');
  const env = tempEnv();
  // Compact Secret payload differs from export's pretty-printed canonical form.
  fs.writeFileSync(env.OPENCLAW_AUTH_FILE, JSON.stringify(store('baseline-stable')));
  const compactHash = crypto.createHash('sha256')
    .update(fs.readFileSync(env.OPENCLAW_AUTH_FILE)).digest('hex');

  assert.equal(importToSqlite(env), true);
  assert.equal(exportFromSqlite(env), true);
  const normalized = fs.readFileSync(env.OPENCLAW_AUTH_FILE);
  const baselineHash = crypto.createHash('sha256').update(normalized).digest('hex');
  assert.notEqual(baselineHash, compactHash);

  assert.equal(exportFromSqlite(env), false);
  assert.equal(
    crypto.createHash('sha256').update(fs.readFileSync(env.OPENCLAW_AUTH_FILE)).digest('hex'),
    baselineHash
  );
});
