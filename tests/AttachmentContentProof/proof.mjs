import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { AttachmentMaterializer: Fixed } = require('../../agent-runtime/files/materialize.js');
const { AttachmentMaterializer: Baseline } = require(path.resolve(process.env.PROOF_BASELINE));
const base = process.env.PROOF_BASE || 'http://127.0.0.1:8080';
const callback = `${base}/internal/sessions/s1`;
const headers = { 'X-Agent-Token': 'proof-token', 'Content-Type': 'application/json' };
const bytes = Buffer.alloc(2 * 1024 * 1024 + 17);
for (let i = 0; i < bytes.length; i++) bytes[i] = i % 251;
const root = await fs.mkdtemp(path.join(os.tmpdir(), 'attachment-content-proof-'));
const hash = value => createHash('sha256').update(value).digest('hex');
async function call(url, options = {}) {
  const response = await fetch(url, { ...options, headers });
  assert.equal(response.status, 200, `${options.method || 'GET'} ${new URL(url).pathname}: ${response.status}`);
  return response;
}
try {
  const reservation = await (await call(`${callback}/files/reserve`, {
    method: 'POST', body: JSON.stringify({ name: 'attachment.bin', mimeType: 'application/octet-stream', size: bytes.length })
  })).json();
  const id = reservation.file.id;
  // Upload through the real proxy route so the fixture itself needs no public storage access.
  const upload = await fetch(`${callback}/files/${id}/content`, {
    method: 'PUT', headers: { 'X-Agent-Token': 'proof-token', 'Content-Type': 'application/octet-stream' }, body: bytes
  });
  assert.equal(upload.status, 204);
  await call(`${callback}/files/${id}/complete`, { method: 'POST' });
  const metadata = await (await call(`${callback}/files/materialize`, {
    method: 'POST', body: JSON.stringify({ fileIds: [id] })
  })).json();
  assert.equal(new URL(metadata[0].downloadUrl).port, '3919');
  await assert.rejects(fetch(metadata[0].downloadUrl), error => error.cause?.code === 'ECONNREFUSED');
  const env = { AGENTHUB_CALLBACK_URL: callback, AGENTHUB_CALLBACK_TOKEN: 'proof-token', AGENTHUB_SESSION_ID: 's1' };
  await assert.rejects(new Baseline({ env, managedRoot: path.join(root, 'before') }).materialize([id]),
    error => error.cause?.code === 'ECONNREFUSED');
  console.log('PASS before: unchanged materializer fails with ECONNREFUSED at the presigned endpoint');
  const [attachment] = await new Fixed({ env, managedRoot: path.join(root, 'after') }).materialize([id]);
  const actual = await fs.readFile(attachment.localPath);
  assert.deepEqual(actual, bytes);
  assert.equal(attachment.size, bytes.length);
  console.log(`PASS after: ${actual.length} bytes, sha256=${hash(actual)}, byte-identical`);
  for (const [session, token] of [['s1', 'sibling-token'], ['s2', 'proof-token'], ['s1', ''], ['s1', 'invalid-token']]) {
    const denied = await fetch(`${base}/internal/sessions/${session}/files/${id}/content`, {
      headers: { 'X-Agent-Token': token }, redirect: 'error'
    });
    assert.equal(denied.status, 401);
  }
  const otherFile = await fetch(`${base}/internal/sessions/s2/files/${id}/content`, {
    headers: { 'X-Agent-Token': 'sibling-token' }, redirect: 'error'
  });
  assert.equal(otherFile.status, 404);
  console.log('PASS auth: missing/invalid/foreign tokens=401; own token with foreign file=404');
  console.log('LIMITATION: no real NetworkPolicy tested; public storage endpoint is a closed port');
} finally {
  await fs.rm(root, { recursive: true, force: true });
}
