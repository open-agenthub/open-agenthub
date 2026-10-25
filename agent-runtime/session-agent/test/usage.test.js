'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const usage = require('../../common/usage');
const codex = require('../../codex/driver');
const openclaw = require('../../openclaw/driver');
const opencode = require('../../opencode/driver');

// Records in the shapes the pinned CLIs wrote against a mock model that answered with
// input 1000 (600 of it cached) and output 50 (20 of it reasoning) — docs/usage-tracking.md.
const codexRecord = (responseId, turnId = 't1') => JSON.stringify({ type: 'token_usage_record', ordinal: 10, payload: {
  thread_id: 'th1', turn_id: turnId, response_id: responseId,
  usage: { input_tokens: 1000, cached_input_tokens: 600, cache_write_input_tokens: 0, output_tokens: 50, reasoning_output_tokens: 20, total_tokens: 1050 } } });
const codexTurn = JSON.stringify({ type: 'event_msg', payload: { type: 'task_started', turn_id: 't1' } });

function scratch() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'usage-test-'));
}

test('Codex usage sums token_usage_record per response and maps cached input to cache reads', t => {
  const home = scratch();
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  const dir = path.join(home, 'sessions', '2026', '10', '25');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, 'rollout-2026-10-25T18-53-17-th1.jsonl');
  fs.writeFileSync(file, [JSON.stringify({ type: 'session_meta', payload: { id: 'th1' } }), codexTurn, codexRecord('r1')].join('\n') + '\n');

  assert.deepEqual(codex.readUsage({ env: { CODEX_HOME: home }, fs }), {
    inputTokens: 400, outputTokens: 50, cacheReadTokens: 600, cacheCreationTokens: 0,
    reasoningTokens: 20, turns: 1, costUsd: null });

  // Appended: a second turn, a half-written line, and the first response written again.
  fs.appendFileSync(file, codexTurn + '\n' + codexRecord('r2') + '\n' + codexRecord('r1') + '\n' + codexRecord('r3').slice(0, 40));
  const second = codex.readUsage({ env: { CODEX_HOME: home }, fs });
  assert.equal(second.inputTokens, 800);
  assert.equal(second.turns, 2);
  fs.appendFileSync(file, codexRecord('r3').slice(40) + '\n');
  assert.equal(codex.readUsage({ env: { CODEX_HOME: home }, fs }).inputTokens, 1200);
});

test('Codex usage is null before any rollout exists', () => {
  assert.equal(codex.readUsage({ env: { CODEX_HOME: path.join(os.tmpdir(), 'no-such-codex-home') }, fs }), null);
});

test('a rewritten (shorter) file is read again from the start instead of skipped', t => {
  const home = scratch();
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  const file = path.join(home, 'a.jsonl');
  const read = usage.createJsonlUsageReader(usage.codexUsageRecord);
  fs.writeFileSync(file, codexRecord('r1') + '\n' + codexRecord('r2') + '\n');
  assert.equal(read([file], fs).inputTokens, 800);
  fs.writeFileSync(file, codexRecord('r9') + '\n');
  assert.equal(read([file], fs).inputTokens, 400);
});

test('OpenClaw usage reads sessions/<id>.jsonl, skips the trajectory copy and keeps its cost', t => {
  const home = scratch();
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  const dir = path.join(home, '.openclaw', 'agents', 'main', 'sessions');
  fs.mkdirSync(dir, { recursive: true });
  const lines = [
    JSON.stringify({ type: 'session', id: 's1' }),
    JSON.stringify({ type: 'message', id: 'u1', message: { role: 'user', content: 'say hi' } }),
    JSON.stringify({ type: 'message', id: 'a1', message: { role: 'assistant', usage: { input: 400, output: 50, cacheRead: 600, cacheWrite: 0,
      reasoningTokens: 20, totalTokens: 1050, cost: { total: 0.00213 } } } })
  ].join('\n') + '\n';
  fs.writeFileSync(path.join(dir, 's1.jsonl'), lines);
  fs.writeFileSync(path.join(dir, 's1.trajectory.jsonl'), lines);

  assert.deepEqual(openclaw.readUsage({ env: {}, home, fs }), {
    inputTokens: 400, outputTokens: 50, cacheReadTokens: 600, cacheCreationTokens: 0,
    reasoningTokens: 20, turns: 1, costUsd: 0.00213 });
});

test('OpenCode usage adds reasoning to output and counts only top-level user turns', () => {
  let sql = '';
  const fakeDb = { prepare(text) { sql = text; return { get: () => ({
    input: 400, output: 30, reasoning: 20, cache_read: 600, cache_write: 0, cost: 0.00213, responses: 1, turns: 1 }) }; },
  close() {} };
  const result = usage.readOpenCodeUsage('/data/opencode.db', {
    fs: { statSync: () => ({ isFile: () => true }) }, openDatabase: () => fakeDb });
  assert.deepEqual(result, { inputTokens: 400, outputTokens: 50, cacheReadTokens: 600, cacheCreationTokens: 0,
    reasoningTokens: 20, turns: 1, costUsd: 0.00213 });
  assert.match(sql, /s\.parent_id IS NULL/);
});

test('OpenCode usage is null without a database and survives one it cannot read', () => {
  assert.equal(usage.readOpenCodeUsage('/nope', { fs: { statSync() { throw new Error('ENOENT'); } } }), null);
  assert.equal(usage.readOpenCodeUsage('/data/opencode.db', { fs: { statSync: () => ({ isFile: () => true }) },
    openDatabase() { throw new Error('database is locked'); } }), null);
});

test('OpenCode usage against a real SQLite database in the CLI schema', { skip: !hasSqlite() }, t => {
  const home = scratch();
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  const { DatabaseSync } = require('node:sqlite');
  const file = path.join(home, 'opencode.db');
  const db = new DatabaseSync(file);
  db.exec('CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT); CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, data TEXT);');
  db.exec("INSERT INTO session VALUES ('top', NULL), ('sub', 'top')");
  const insert = db.prepare('INSERT INTO message VALUES (?, ?, ?)');
  insert.run('m1', 'top', JSON.stringify({ role: 'user' }));
  insert.run('m2', 'top', JSON.stringify({ role: 'assistant', cost: 0.002, tokens: { input: 400, output: 30, reasoning: 20, cache: { read: 600, write: 0 } } }));
  insert.run('m3', 'sub', JSON.stringify({ role: 'user' }));
  insert.run('m4', 'sub', JSON.stringify({ role: 'assistant', cost: 0.001, tokens: { input: 100, output: 10, reasoning: 0, cache: { read: 0, write: 5 } } }));
  db.close();

  assert.deepEqual(opencode.readUsage({ env: { OPENCODE_HOME: home }, home: '/unused', fs }), {
    inputTokens: 500, outputTokens: 60, cacheReadTokens: 600, cacheCreationTokens: 5,
    reasoningTokens: 20, turns: 1, costUsd: 0.003 });
});

function hasSqlite() {
  try { require('node:sqlite'); return true; } catch { return false; }
}
