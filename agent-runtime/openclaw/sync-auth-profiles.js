'use strict';

const fs = require('node:fs');
const path = require('node:path');

const PRIMARY_ROW_KEY = 'primary';

const AGENT_SCHEMA_SQL = `
CREATE TABLE IF NOT EXISTS auth_profile_store (
  store_key TEXT NOT NULL PRIMARY KEY,
  store_json TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);
`;

function resolvePaths(env = process.env) {
  const stateDir = env.OPENCLAW_STATE_DIR || path.join(env.HOME || '', '.openclaw');
  const agentId = env.AGENTHUB_OPENCLAW_AGENT_ID || 'main';
  const agentDir = env.OPENCLAW_AGENT_DIR || path.join(stateDir, 'agents', agentId, 'agent');
  const authFile = env.OPENCLAW_AUTH_FILE || path.join(stateDir, 'auth-profiles.json');
  const sqlitePath = path.join(agentDir, 'openclaw-agent.sqlite');
  return { stateDir, agentId, agentDir, authFile, sqlitePath };
}

function validStore(value) {
  return value !== null && !Array.isArray(value) && typeof value === 'object' &&
    value.profiles !== null && !Array.isArray(value.profiles) &&
    typeof value.profiles === 'object' &&
    Object.keys(value.profiles).length > 0;
}

function openDatabase(sqlitePath, readOnly) {
  const { DatabaseSync } = require('node:sqlite');
  return new DatabaseSync(sqlitePath, readOnly ? { readOnly: true } : {});
}

function readStoreFromSqlite(sqlitePath) {
  if (!fs.existsSync(sqlitePath)) return null;
  let db;
  try {
    db = openDatabase(sqlitePath, true);
    const row = db.prepare(
      'SELECT store_json FROM auth_profile_store WHERE store_key = ?'
    ).get(PRIMARY_ROW_KEY);
    if (!row || typeof row.store_json !== 'string') return null;
    return JSON.parse(row.store_json);
  } catch {
    return null;
  } finally {
    if (db) db.close();
  }
}

function writeStoreToSqlite(sqlitePath, store) {
  fs.mkdirSync(path.dirname(sqlitePath), { recursive: true, mode: 0o700 });
  const db = openDatabase(sqlitePath, false);
  try {
    db.exec(AGENT_SCHEMA_SQL);
    db.prepare(`
      INSERT INTO auth_profile_store (store_key, store_json, updated_at)
      VALUES (?, ?, ?)
      ON CONFLICT(store_key) DO UPDATE SET
        store_json = excluded.store_json,
        updated_at = excluded.updated_at
    `).run(PRIMARY_ROW_KEY, JSON.stringify(store), Date.now());
  } finally {
    db.close();
  }
}

/** Export SQLite store_json → AgentHub-managed auth-profiles.json when present/newer. */
function exportFromSqlite(env = process.env) {
  if (!env.OPENCLAW_AUTH_FILE) return false;
  const { authFile, sqlitePath, agentDir } = resolvePaths(env);
  const store = readStoreFromSqlite(sqlitePath);
  if (!validStore(store)) return false;

  const body = JSON.stringify(store, null, 2) + '\n';
  let existing = '';
  try { existing = fs.readFileSync(authFile, 'utf8'); } catch { /* missing */ }
  if (existing === body) return false;

  fs.mkdirSync(path.dirname(authFile), { recursive: true, mode: 0o700 });
  fs.writeFileSync(authFile, body, { mode: 0o600 });
  // Keep a sibling copy under the agent dir for tools that still look for the logical JSON path.
  fs.mkdirSync(agentDir, { recursive: true, mode: 0o700 });
  fs.writeFileSync(path.join(agentDir, 'auth-profiles.json'), body, { mode: 0o600 });
  return true;
}

/** Import AgentHub Secret JSON into the default agent SQLite store OpenClaw loads at runtime. */
function importToSqlite(env = process.env) {
  if (!env.OPENCLAW_AUTH_FILE) return false;
  const { authFile, sqlitePath, agentDir } = resolvePaths(env);
  if (!fs.existsSync(authFile)) return false;
  const store = JSON.parse(fs.readFileSync(authFile, 'utf8'));
  if (!validStore(store)) throw new Error('OpenClaw auth-profiles.json is missing a non-empty profiles object');

  fs.mkdirSync(agentDir, { recursive: true, mode: 0o700 });
  fs.writeFileSync(path.join(agentDir, 'auth-profiles.json'),
    JSON.stringify(store, null, 2) + '\n', { mode: 0o600 });
  writeStoreToSqlite(sqlitePath, store);
  return true;
}

function main(argv = process.argv.slice(2)) {
  const command = argv[0];
  if (command === 'export') {
    exportFromSqlite();
    return;
  }
  if (command === 'import') {
    importToSqlite();
    return;
  }
  console.error('Usage: sync-auth-profiles.js <export|import>');
  process.exit(1);
}

if (require.main === module) main();

module.exports = {
  resolvePaths, validStore, exportFromSqlite, importToSqlite, readStoreFromSqlite, writeStoreToSqlite
};
