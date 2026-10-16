import test from 'node:test';
import assert from 'node:assert/strict';

const env = () => ({
  AGENTHUB_URL: 'https://hub.example.com',
  AGENTHUB_TOKEN: 'oah_secret_never_leak'
});

function recordingFetch(calls, respond) {
  return async (url, init) => {
    calls.push({ url, init });
    return respond(url, init);
  };
}

test('sharing calls hit the remote shares routes with the bearer token', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const calls = [];
  const client = new AgentHubClient(env(), recordingFetch(calls, (url, init) => {
    if (init.method === 'DELETE') return new Response(null, { status: 204 });
    if (url.endsWith('/links')) {
      return Response.json({ link: { id: 'link-1', role: 'Collaborator', expiresAt: null }, url: 'https://hub.example.com/shared/tok' });
    }
    if (url.endsWith('/shares')) return Response.json({ users: [], links: [], mcpPolicy: null });
    return Response.json({ recipient: 'bob', role: 'Viewer' });
  }));

  assert.equal((await client.shareWithUser('s 1', 'bob')).role, 'Viewer');
  await client.shareWithUser('s 1', 'bob', 'Collaborator');
  assert.deepEqual(await client.unshareUser('s 1', 'b/ob'), { recipient: 'b/ob', removed: true });
  const link = await client.createShareLink('s 1', { role: 'Collaborator', expiresAt: '2030-01-01T00:00:00Z' });
  assert.equal(link.url, 'https://hub.example.com/shared/tok');
  await client.createShareLink('s 1');
  assert.deepEqual(await client.deleteShareLink('s 1', 'link-1'), { linkId: 'link-1', removed: true });
  assert.deepEqual(await client.listShares('s 1'), { users: [], links: [], mcpPolicy: null });

  assert.deepEqual(calls.map(c => [c.init.method, c.url.replace('https://hub.example.com', ''), c.init.body]), [
    ['POST', '/api/remote/sessions/s%201/shares/users', JSON.stringify({ recipient: 'bob', role: 'Viewer' })],
    ['POST', '/api/remote/sessions/s%201/shares/users', JSON.stringify({ recipient: 'bob', role: 'Collaborator' })],
    ['DELETE', '/api/remote/sessions/s%201/shares/users/b%2Fob', undefined],
    ['POST', '/api/remote/sessions/s%201/shares/links', JSON.stringify({ role: 'Collaborator', expiresAt: '2030-01-01T00:00:00Z' })],
    ['POST', '/api/remote/sessions/s%201/shares/links', JSON.stringify({ role: 'Viewer', expiresAt: null })],
    ['DELETE', '/api/remote/sessions/s%201/shares/links/link-1', undefined],
    ['GET', '/api/remote/sessions/s%201/shares', undefined]
  ]);
  assert.ok(calls.every(c => c.init.headers.Authorization === 'Bearer oah_secret_never_leak'));
});

test('listSharedWithMe reads the dedicated route, not the session listing', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const calls = [];
  const client = new AgentHubClient(env(), recordingFetch(calls, () =>
    Response.json([{ id: 's9', title: 'Theirs', accessRole: 'Viewer', sharedBy: 'carol' }])));

  const shared = await client.listSharedWithMe();

  assert.equal(shared[0].sharedBy, 'carol');
  assert.equal(calls[0].url, 'https://hub.example.com/api/remote/sessions/shared');
  assert.equal(calls[0].init.method, 'GET');
});

test('a backend error code is attached to the error, the message text is not', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const client = new AgentHubClient(env(), async () =>
    Response.json({ error: 'Recipient is not a known user. (secret details)', code: 'unknown_recipient' }, { status: 400 }));

  await assert.rejects(() => client.shareWithUser('s1', 'ghost'), error => {
    assert.equal(error.message, 'agenthub_http_400');
    assert.equal(error.code, 'unknown_recipient');
    assert.doesNotMatch(JSON.stringify(error), /secret details/);
    return true;
  });
});

test('an error body without a code, or that is not JSON, leaves the error uncoded', async () => {
  const { AgentHubClient } = await import('../client.mjs');

  const plain = new AgentHubClient(env(), async () => new Response('nope', { status: 402 }));
  await assert.rejects(() => plain.listShares('s1'), error => error.message === 'agenthub_http_402' && error.code === undefined);

  const uncoded = new AgentHubClient(env(), async () => Response.json({ error: 'Expiration must be in the future.' }, { status: 400 }));
  await assert.rejects(() => uncoded.createShareLink('s1'), error => error.message === 'agenthub_http_400' && error.code === undefined);
});

test('sharing errors map to the vocabulary the remote MCP server uses', async () => {
  const { sharingErrorCode, safeError } = await import('../errors.mjs');

  assert.equal(sharingErrorCode(new Error('agenthub_http_402')), 'license_required');
  assert.equal(sharingErrorCode(new Error('agenthub_http_404')), 'session_not_found');
  assert.equal(sharingErrorCode(Object.assign(new Error('agenthub_http_400'), { code: 'unknown_recipient' })), 'unknown_recipient');
  // A bare 400 says nothing a caller could act on; the status stays visible.
  assert.equal(sharingErrorCode(new Error('agenthub_http_400')), 'agenthub_http_400');
  assert.equal(sharingErrorCode(new Error('agenthub_response_too_large')), 'agenthub_response_too_large');
  assert.equal(sharingErrorCode(new Error('boom')), 'agenthub_operation_failed');

  // A code is trusted for its shape only: nothing that could carry text reaches the model.
  assert.equal(safeError(Object.assign(new Error('agenthub_http_400'), { code: 'unknown_recipient' })), 'unknown_recipient');
  assert.equal(safeError(Object.assign(new Error('agenthub_http_400'), { code: 'Recipient bob: not found' })), 'agenthub_http_400');
  assert.equal(safeError(Object.assign(new Error('agenthub_http_400'), { code: 'x'.repeat(65) })), 'agenthub_http_400');
  assert.equal(safeError(new Error('agenthub_http_500')), 'agenthub_http_500');
  assert.equal(safeError(new Error('token oah_secret_never_leak rejected')), 'agenthub_operation_failed');
});
