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
    fetch: async (url, init) => {
      assert.equal(init.headers['X-Agent-Token'], 'token');
      if (url.endsWith('/files/materialize')) return json([{ id: firstId, sessionId, name: 'shot.png', mimeType: 'image/png', size: 4,
        state: 'Ready', storageKind: 'S3', downloadUrl: 'https://example.com:3910/file' }]);
      assert.equal(url, `http://backend/internal/sessions/${sessionId}/files/${firstId}/content`);
      assert.equal(init.redirect, 'error');
      return new Response(new Uint8Array([1, 2, 3, 4]));
    }
  });

  const [result] = await materializer.materialize([firstId]);

  assert.deepEqual(await fs.promises.readFile(result.localPath), Buffer.from([1, 2, 3, 4]));
});

for (const [label, response, expected] of [
  ['declared oversize', () => new Response('12345', { headers: { 'content-length': '5' } }), /attachment_bytes_exceeded/],
  ['chunked oversize', () => new Response(new ReadableStream({ start(c) { c.enqueue(new Uint8Array(5)); c.close(); } })), /file_too_large/],
  ['truncated content', () => new Response(new Uint8Array(3)), /attachment_content_invalid/],
  ['unauthorized content', () => new Response('', { status: 401 }), /attachment_download_failed/],
]) {
  test(`materializer rejects ${label} and leaves no cached file`, async t => {
    const { AttachmentMaterializer } = require('../../files/materialize');
    const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-materialize-bounds-'));
    t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
    const materializer = new AttachmentMaterializer({
      env: {
        AGENTHUB_CALLBACK_URL: `http://backend/internal/sessions/${sessionId}`,
        AGENTHUB_CALLBACK_TOKEN: 'token', AGENTHUB_SESSION_ID: sessionId
      },
      managedRoot: root,
      fetch: async url => url.endsWith('/materialize')
        ? json([{ id: firstId, sessionId, name: 'shot.png', mimeType: 'image/png', size: 4,
          state: 'Ready', storageKind: 'S3' }])
        : response()
    });
    await assert.rejects(() => materializer.materialize([firstId]), expected);
    assert.equal(await materializer.store.head(firstId), null);
  });
}

test('materializer refuses a real HTTP redirect without forwarding the callback token', async t => {
  const http = require('node:http');
  const { AttachmentMaterializer } = require('../../files/materialize');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-materialize-redirect-'));
  let redirectedRequests = 0;
  const server = http.createServer((request, response) => {
    if (request.url === '/redirect-target') {
      redirectedRequests++;
      response.end('1234');
    } else if (request.url.endsWith('/materialize')) {
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify([{ id: firstId, sessionId, name: 'shot.png', mimeType: 'image/png',
        size: 4, state: 'Ready', storageKind: 'S3' }]));
    } else {
      assert.equal(request.headers['x-agent-token'], 'token');
      response.writeHead(307, { Location: '/redirect-target' });
      response.end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    await fs.promises.rm(root, { recursive: true, force: true });
  });
  const materializer = new AttachmentMaterializer({
    env: {
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${server.address().port}/internal/sessions/${sessionId}`,
      AGENTHUB_CALLBACK_TOKEN: 'token', AGENTHUB_SESSION_ID: sessionId
    },
    managedRoot: root
  });
  await assert.rejects(() => materializer.materialize([firstId]), /fetch failed/);
  assert.equal(redirectedRequests, 0);
  assert.equal(await materializer.store.head(firstId), null);
});
