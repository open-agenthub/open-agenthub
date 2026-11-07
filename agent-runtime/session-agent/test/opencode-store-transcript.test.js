'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const { exportTranscript, PAGE_MESSAGES } = require('../../opencode/store-transcript');

function fixture(t) {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'opencode-store-'));
  fs.mkdirSync(path.join(home, '.opencode'));
  const db = new DatabaseSync(path.join(home, '.opencode/opencode.db'));
  db.exec('CREATE TABLE session(id TEXT PRIMARY KEY, parent_id TEXT); CREATE TABLE message(id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT); CREATE TABLE part(id TEXT PRIMARY KEY, message_id TEXT, data TEXT)');
  db.prepare('INSERT INTO session VALUES (?, NULL)').run('ses_test');
  t.after(() => { db.close(); fs.rmSync(home, { recursive: true, force: true }); });
  const insert = (n, role = 'user', parts = [{ type: 'text', text: 'message ' + n }], completed = 1) => {
    const id = 'msg_' + String(n).padStart(8, '0');
    db.prepare('INSERT INTO message VALUES (?, ?, ?, ?)').run(id, 'ses_test', n, JSON.stringify({ role, time: { completed } }));
    for (let i = 0; i < parts.length; i++) db.prepare('INSERT INTO part VALUES (?, ?, ?)').run('prt_' + id + '_' + String(i).padStart(3, '0'), id, JSON.stringify(parts[i]));
  };
  const record = () => exportTranscript({ home, sessionID: 'ses_test' });
  const rows = () => fs.readFileSync(record(), 'utf8').trim().split('\n').map(JSON.parse);
  return { home, db, insert, record, rows };
}

test('native keyset pages survive reconnect and a torn journal append without duplicates', t => {
  const f = fixture(t);
  for (let n = 1; n <= 305; n++) f.insert(n);
  const file = f.record();
  assert.equal(fs.readFileSync(file, 'utf8').trim().split('\n').length, PAGE_MESSAGES);
  fs.appendFileSync(file, '{"interrupted":');
  f.record(); f.record();
  const rows = f.rows();
  assert.equal(rows.length, 305);
  assert.equal(new Set(rows.map(x => x.uuid)).size, 305);
  assert.equal(rows[0].message.content[0].text, 'message 1');
  assert.equal(f.rows().length, 305);
});

test('unfinished native answers block later messages and resume when completed', t => {
  const f = fixture(t);
  f.insert(1); f.insert(2, 'assistant', [{ type: 'text', text: 'streaming' }], null); f.insert(3);
  assert.equal(f.rows().length, 1);
  f.db.exec("UPDATE message SET data = json_set(data, '$.time.completed', 1) WHERE id = 'msg_00000002'");
  assert.equal(f.rows().length, 3);
});

test('tool calls, failures, files and large output use the existing bounded transcript format', t => {
  const f = fixture(t);
  f.insert(1, 'assistant', [
    { type: 'text', text: 'x'.repeat(100_000) },
    { type: 'tool', tool: 'bash', callID: 'c1', state: { status: 'completed', input: { command: 'echo ok' }, output: 'y'.repeat(100_000) } },
    { type: 'tool', tool: 'read', callID: 'c2', state: { status: 'error', input: { filePath: 'missing' }, error: 'missing file' } }
  ]);
  const row = f.rows()[0];
  assert.equal(row.message.content[1].type, 'tool_use');
  assert.equal(row.message.content[2].type, 'tool_result');
  assert.match(row.message.content[2].content, /content not shown|Large content not shown/);
  assert.equal(row.message.content[4].is_error, true);
  assert.ok(JSON.stringify(row).length < 60_000);
  f.db.exec("UPDATE session SET parent_id = 'ses_parent'");
  f.insert(2);
  assert.equal(f.rows().length, 1);
});

test('a user row created before its parts cannot advance the durable cursor', t => {
  const f = fixture(t);
  f.insert(1, 'user', []);
  assert.equal(f.record(), null);
  f.db.prepare('INSERT INTO part VALUES (?, ?, ?)').run('prt_1', 'msg_00000001', JSON.stringify({ type: 'text', text: 'now persisted' }));
  assert.equal(f.rows().length, 1);
});

test('schema mismatch falls back to the existing snapshot with a diagnostic and no crash', t => {
  const f = fixture(t);
  const root = path.join(f.home, '.opencode');
  fs.mkdirSync(path.join(root, 'agenthub-transcripts'));
  const snapshot = path.join(root, 'agenthub-transcripts/ses_test.jsonl');
  fs.writeFileSync(snapshot, '{"type":"user","message":{"content":"fallback"}}\n');
  fs.writeFileSync(path.join(root, 'agenthub-session-id'), 'ses_test');
  f.db.exec('DROP TABLE part');
  f.insert(1, 'user', []);
  const logs = [];
  const original = console.log;
  console.log = value => logs.push(value);
  t.after(() => { console.log = original; });
  const driver = require('../../opencode/driver');
  assert.equal(driver.findTranscript({ env: { HOME: f.home }, home: f.home, fs }), snapshot);
  assert(logs.some(line => line.includes('using snapshot history')));
});

test('an exclusive native writer cannot make the exporter wait or modify its database', t => {
  const f = fixture(t);
  f.insert(1);
  f.db.exec('BEGIN EXCLUSIVE');
  let unavailable = false;
  const started = Date.now();
  assert.equal(exportTranscript({ home: f.home, sessionID: 'ses_test', onUnavailable() { unavailable = true; } }), null);
  assert(unavailable);
  assert(Date.now() - started < 1000);
  f.db.exec('ROLLBACK');
  assert.equal(f.rows().length, 1);
});

test('masking precedes truncation and oversized fields never expose a partial credential', t => {
  const f = fixture(t);
  const secret = 'private-secret-for-output';
  f.insert(1, 'assistant', [
    { type: 'text', text: 'x'.repeat(49990) + secret },
    { type: 'tool', tool: 'bash', callID: 'c1', state: { status: 'completed', input: { command: 'echo ' + secret }, output: 'x'.repeat(1990) + secret } }
  ]);
  const file = exportTranscript({ home: f.home, sessionID: 'ses_test', env: { AGENTHUB_SECRET_ENV: 'PROOF_SECRET', PROOF_SECRET: secret } });
  const raw = fs.readFileSync(file, 'utf8');
  assert(!raw.includes(secret));
  assert(!raw.includes('private-'));
  assert(raw.includes('Large content not shown'));
});
