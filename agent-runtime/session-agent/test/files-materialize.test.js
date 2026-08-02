const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const sessionId = 'session-1';
const firstId = 'a'.repeat(32);

function json(value) {
  return new Response(JSON.stringify(value), { headers: { 'content-type': 'application/json' } });
}

test('materializer rejects incomplete, cross-session, and oversized batches', async t => {
  const { AttachmentMaterializer } = require('../../files/materialize');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-materialize-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const base = {
    env: {
      AGENTHUB_CALLBACK_URL: `http://backend/internal/sessions/${sessionId}`,
      AGENTHUB_CALLBACK_TOKEN: 'token', AGENTHUB_SESSION_ID: sessionId
    },
    managedRoot: root
  };

  const missing = new AttachmentMaterializer({ ...base, fetch: async () => json([]) });
  await assert.rejects(() => missing.materialize([firstId]), /attachment_batch_invalid/);

  const crossed = new AttachmentMaterializer({ ...base, fetch: async () => json([{
    id: firstId, sessionId: 'session-2', name: 'shot.png', mimeType: 'image/png',
    size: 4, state: 'Ready', storageKind: 'Pod', locator: `${firstId}/shot.png`
  }]) });
  await assert.rejects(() => crossed.materialize([firstId]), /attachment_batch_invalid/);

  const oversized = new AttachmentMaterializer({ ...base, maxTotalBytes: 3, fetch: async () => json([{
    id: firstId, sessionId, name: 'shot.png', mimeType: 'image/png',
    size: 4, state: 'Ready', storageKind: 'Pod', locator: `${firstId}/shot.png`
  }]) });
  await assert.rejects(() => oversized.materialize([firstId]), /attachment_bytes_exceeded/);
});

test('materializer reuses an exact pod locator without accepting an absolute path', async t => {
  const { AttachmentMaterializer } = require('../../files/materialize');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-materialize-pod-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const directory = path.join(root, firstId);
  await fs.promises.mkdir(directory);
  await fs.promises.writeFile(path.join(directory, 'shot.png'), Buffer.from([1, 2, 3, 4]));
  let body;
  const materializer = new AttachmentMaterializer({
    env: {
      AGENTHUB_CALLBACK_URL: `http://backend/internal/sessions/${sessionId}`,
      AGENTHUB_CALLBACK_TOKEN: 'token', AGENTHUB_SESSION_ID: sessionId
    },
    managedRoot: root,
    fetch: async (_url, init) => {
      body = JSON.parse(init.body);
      return json([{ id: firstId, sessionId, name: 'shot.png', mimeType: 'image/png',
        size: 4, state: 'Ready', storageKind: 'Pod', locator: `${firstId}/shot.png` }]);
    }
  });

  const result = await materializer.materialize([firstId]);

  assert.deepEqual(body, { fileIds: [firstId] });
  assert.equal(result[0].localPath, await fs.promises.realpath(path.join(root, firstId, 'shot.png')));
  assert.equal(Object.isFrozen(result[0]), true);
});

test('materializer downloads persistent content with exact bounded size', async t => {
  const { AttachmentMaterializer } = require('../../files/materialize');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-materialize-s3-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const materializer = new AttachmentMaterializer({
    env: {
      AGENTHUB_CALLBACK_URL: `http://backend/internal/sessions/${sessionId}`,
      AGENTHUB_CALLBACK_TOKEN: 'token', AGENTHUB_SESSION_ID: sessionId
    },
    managedRoot: root,
    fetch: async (url, init) => url.startsWith('http://backend')
      ? json([{ id: firstId, sessionId, name: 'shot.png', mimeType: 'image/png', size: 4,
        state: 'Ready', storageKind: 'S3', downloadUrl: 'https://storage.invalid/file' }])
      : new Response(new Uint8Array([1, 2, 3, 4]))
  });

  const [result] = await materializer.materialize([firstId]);

  assert.deepEqual(await fs.promises.readFile(result.localPath), Buffer.from([1, 2, 3, 4]));
});
