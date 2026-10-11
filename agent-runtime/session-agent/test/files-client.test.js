const assert = require('node:assert/strict');
const test = require('node:test');

const env = {
  AGENTHUB_CALLBACK_URL: 'http://backend/internal/sessions/session-1',
  AGENTHUB_CALLBACK_TOKEN: 'secret-token'
};

function response(body, status = 200) {
  return new Response(body, { status, headers: { 'content-type': 'application/json' } });
}

test('files client rejects oversized JSON and keeps the token in its header', async () => {
  const { FilesBackendClient } = await import('../../files/client.mjs');
  let request;
  const client = new FilesBackendClient(env, async (url, init) => {
    request = { url, init };
    return response(`"${'x'.repeat(1_000_001)}"`);
  });

  await assert.rejects(() => client.list(), /files_backend_response_too_large/);
  assert.equal(request.url, `${env.AGENTHUB_CALLBACK_URL}/files`);
  assert.equal(request.init.headers['X-Agent-Token'], 'secret-token');
  assert.doesNotMatch(request.url, /secret-token/);
});

test('files client uploads bytes through the returned descriptor and completes the record', async () => {
  const { FilesBackendClient } = await import('../../files/client.mjs');
  const requests = [];
  const client = new FilesBackendClient(env, async (url, init) => {
    requests.push({ url, init });
    if (url.endsWith('/files/reserve')) return response(JSON.stringify({
      file: { id: 'a'.repeat(32), name: 'out.png' },
      upload: { kind: 'proxy', url: '/internal/sessions/session-1/files/a/content', headers: {} }
    }));
    if (url.endsWith('/complete')) return response(JSON.stringify({ id: 'a'.repeat(32), state: 'Ready' }));
    return new Response(null, { status: 204 });
  });

  const reserved = await client.reserve({ name: 'out.png', mimeType: 'image/png', size: 3 });
  await client.upload(reserved.upload, new Uint8Array([1, 2, 3]), 'image/png');
  const completed = await client.complete(reserved.file.id);

  assert.equal(completed.state, 'Ready');
  assert.equal(requests[1].init.headers['X-Agent-Token'], 'secret-token');
  assert.deepEqual(new Uint8Array(await new Response(requests[1].init.body).arrayBuffer()), new Uint8Array([1, 2, 3]));
});

test('files client reads project files through its own session route and token', async () => {
  // The sibling's id is a path segment under the caller's own session, never the session in the
  // base url: the backend authorises the token against that one and resolves the sibling itself.
  const { FilesBackendClient } = await import('../../files/client.mjs');
  const requests = [];
  const client = new FilesBackendClient(env, async (url, init) => {
    requests.push({ url, init });
    return url.endsWith('/content')
      ? new Response(new Uint8Array([1, 2, 3]))
      : response(JSON.stringify({ files: [], truncated: false }));
  });

  await client.projectFiles();
  await client.projectFiles('sib 1');
  const body = await client.projectContentStream('sib/1', 'a'.repeat(32));

  assert.deepEqual(requests.map(request => request.url), [
    `${env.AGENTHUB_CALLBACK_URL}/files/project`,
    `${env.AGENTHUB_CALLBACK_URL}/files/project?sessionId=sib%201`,
    `${env.AGENTHUB_CALLBACK_URL}/files/project/sib%2F1/${'a'.repeat(32)}/content`
  ]);
  assert.ok(requests.every(request => request.init.method === 'GET'));
  assert.ok(requests.every(request => request.init.headers['X-Agent-Token'] === 'secret-token'));
  assert.deepEqual(new Uint8Array(await new Response(body).arrayBuffer()), new Uint8Array([1, 2, 3]));
});

test('files client reports a refused project file by status', async () => {
  const { FilesBackendClient } = await import('../../files/client.mjs');
  const client = new FilesBackendClient(env, async () => new Response(null, { status: 404 }));

  await assert.rejects(() => client.projectContentStream('sib1', 'a'.repeat(32)), /files_backend_http_404/);
  await assert.rejects(() => client.projectFiles('sib1'), /files_backend_http_404/);
});
