// Inject faults into an isolated proof database and read native history through the real HTTP
// controller. Never point this at an installation database: it temporarily changes constraints.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(path.join(process.env.PROOF_PG_PACKAGE || '/tmp/provider-tooling/sql', 'package.json'));
const { Client } = require('pg');
const base = process.env.PROOF_URL || 'http://127.0.0.1:8082';
const pgUrl = process.env.PROOF_PG_URL || 'postgresql://postgres@127.0.0.1:54329/proof466final';
const parsed = new URL(pgUrl);
assert(parsed.hostname === '127.0.0.1' && parsed.pathname.startsWith('/proof466'), 'use the isolated local proof database');
const capture = process.env.PROOF_CAPTURE;
assert(capture, 'set PROOF_CAPTURE to native Claude/Codex capture output');
const native = fs.readFileSync(path.join(capture, 'claude.jsonl'), 'utf8');
const db = new Client({ connectionString: pgUrl });
await db.connect();
let renamed = false;
let restoreNotNull = false;
async function read(id) {
  const response = await fetch(`${base}/api/sessions/${id}/conversation?offset=0&max=40`, { headers: { cookie: 'proof-user=alice' } });
  assert.equal(response.status, 200, await response.clone().text());
  const page = await response.json();
  assert(page.entries.some(entry => entry.role === 'user' && entry.text.includes('HISTORY-claude-0')));
  assert(!page.entries.some(entry => entry.text.includes('omitted')));
}
try {
  const nullable = await db.query("SELECT is_nullable FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='sessions' AND column_name='agent_session_id'");
  restoreNotNull = nullable.rows[0].is_nullable === 'NO';
  await db.query('ALTER TABLE sessions ALTER COLUMN agent_session_id DROP NOT NULL');
  for (const id of ['legacy-null-proof', 'legacy-rejected-proof']) {
    await db.query(`INSERT INTO sessions(id,owner,mode,callback_token,agent_session_id,status,transcript)
      VALUES ($1,'alice','Interactive','synthetic-legacy-proof',$2,'Paused',$3)`,
      [id, id === 'legacy-null-proof' ? null : 'native-proof', native]);
  }
  await read('legacy-null-proof');
  const nativeId = await db.query("SELECT native_id FROM session_executions WHERE session_id='legacy-null-proof'");
  assert.equal(nativeId.rows[0].native_id, '');
  await db.query(`CREATE FUNCTION reject_legacy_proof() RETURNS trigger LANGUAGE plpgsql AS $$
    BEGIN
      IF NEW.session_id = 'legacy-rejected-proof' THEN
        RAISE EXCEPTION 'synthetic lazy migration unavailable' USING ERRCODE='42501';
      END IF;
      RETURN NEW;
    END; $$`);
  await db.query('CREATE TRIGGER reject_legacy_proof BEFORE INSERT ON session_executions FOR EACH ROW EXECUTE FUNCTION reject_legacy_proof()');
  await read('legacy-rejected-proof');
  assert.equal((await db.query("SELECT count(*) FROM session_executions WHERE session_id='legacy-rejected-proof'")).rows[0].count, '0');
  await db.query('DROP TRIGGER reject_legacy_proof ON session_executions');
  await read('legacy-rejected-proof');
  assert.equal((await db.query("SELECT count(*) FROM session_executions WHERE session_id='legacy-rejected-proof'")).rows[0].count, '1');
  await db.query('ALTER TABLE session_executions RENAME TO session_executions_proof_unavailable'); renamed = true;
  await read('legacy-null-proof');
  await db.query('ALTER TABLE session_executions_proof_unavailable RENAME TO session_executions'); renamed = false;
  console.log(JSON.stringify({ httpNullNativeId: true, httpRejectedInsert: true, httpRetry: true, httpMissingLedger: true }));
} finally {
  if (renamed) await db.query('ALTER TABLE session_executions_proof_unavailable RENAME TO session_executions');
  await db.query('DROP TRIGGER IF EXISTS reject_legacy_proof ON session_executions');
  await db.query('DROP FUNCTION IF EXISTS reject_legacy_proof()');
  await db.query("DELETE FROM sessions WHERE id IN ('legacy-null-proof','legacy-rejected-proof')");
  if (restoreNotNull) await db.query('ALTER TABLE sessions ALTER COLUMN agent_session_id SET NOT NULL');
  await db.end();
}
