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
