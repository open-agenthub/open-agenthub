'use strict';

// The agent's side of session secrets (docs/session-secrets.md): secrets_list and secret_request
// on agenthub_sessions. The tool learns that a secret is there and under which name; it never
// reads a value, not from the hub and not from ~/.secrets.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const load = () => import('../../sessions/secret-request.mjs');

function fakeClient(answers) {
  const calls = [];
  return {
    calls,
    async secretRequest(body) { calls.push(['create', body]); return answers.create; },
    async secretRequestStatus(id) { calls.push(['status', id]); return answers.status.shift(); },
    async secretRequestExpire(id) { calls.push(['expire', id]); return answers.expire; },
    async secrets() { calls.push(['list']); return answers.list; }
  };
}

function clock() {
  let t = 0;
  return { now: () => t, sleep: async ms => { t += ms; } };
}

test('secret_request waits for the owner and reports where the variable is, never its value', async () => {
  const { requestSecret } = await load();
  const client = fakeClient({
    create: { id: 'r1', status: 'pending' },
    status: [{ id: 'r1', status: 'pending' }, { id: 'r1', status: 'provided', name: 'SENTRY_DSN', delivery: 'env' }]
  });
  const time = clock();
  const result = await requestSecret(client, { name: 'SENTRY_DSN', purpose: 'error reporting' },
    { env: {}, ...time, pollMs: 1000 });

  assert.deepEqual(client.calls[0], ['create', { name: 'SENTRY_DSN', delivery: 'env', purpose: 'error reporting' }]);
  assert.equal(result.status, 'provided');
  assert.equal(result.availableAs, '$SENTRY_DSN');
  // Not in this process's environment yet: a variable arrives with the next start.
  assert.match(result.note, /next start/);
  assert.equal(JSON.stringify(result).includes('value'), false);
});

test('secret_request for a file waits until the file is in the pod', async () => {
  const { requestSecret } = await load();
  const client = fakeClient({ create: { status: 'provided', name: 'sa.json', delivery: 'file' } });
  const time = clock();
  let checks = 0;
  const exists = file => { assert.equal(file, '/home/agent/.secrets/sa.json'); return ++checks > 3; };
  const result = await requestSecret(client, { name: 'sa.json', delivery: 'file' },
    { env: { AGENTHUB_SECRETS_DIR: '/home/agent/.secrets' }, exists, ...time, pollMs: 1000 });

  assert.deepEqual(result, { status: 'provided', name: 'sa.json', delivery: 'file', availableAs: '/home/agent/.secrets/sa.json' });
});

test('a refusal and a timeout are both "not provided", and the timeout closes the request', async () => {
  const { requestSecret } = await load();
  const refused = fakeClient({ create: { id: 'r1', status: 'pending' }, status: [{ id: 'r1', status: 'refused' }] });
  assert.deepEqual(await requestSecret(refused, { name: 'A' }, { env: {}, ...clock() }), { status: 'not provided' });

  const ignored = fakeClient({
    create: { id: 'r2', status: 'pending' },
    status: Array.from({ length: 100 }, () => ({ id: 'r2', status: 'pending' })),
    expire: { id: 'r2', status: 'expired' }
  });
  assert.deepEqual(await requestSecret(ignored, { name: 'A', waitMinutes: 1 }, { env: {}, ...clock(), pollMs: 3000 }),
    { status: 'not provided' });
  assert.deepEqual(ignored.calls.at(-1), ['expire', 'r2']);
});

test('an owner answer that beats the expiry still counts', async () => {
  const { requestSecret } = await load();
  const client = fakeClient({
    create: { id: 'r3', status: 'pending' },
    status: [{ id: 'r3', status: 'pending' }],
    expire: { id: 'r3', status: 'provided', name: 'A_TOKEN', delivery: 'env' }
  });
  const result = await requestSecret(client, { name: 'A_TOKEN', waitMinutes: 0 }, { env: { A_TOKEN: 'x' }, ...clock() });
  assert.deepEqual(result, { status: 'provided', name: 'A_TOKEN', delivery: 'env', availableAs: '$A_TOKEN' });
});

test('secrets_list names the secrets and where to find them, without reading them', async () => {
  const { listSecrets } = await load();
  const client = fakeClient({ list: { secrets: [
    { id: 's1', name: 'DEPLOY_TOKEN', delivery: 'env', description: 'CI deploy' },
    { id: 's2', name: 'sa.json', delivery: 'file', description: null }
  ] } });
  const result = await listSecrets(client, { HOME: '/home/agent', DEPLOY_TOKEN: 'never-shown' }, () => true);
  assert.deepEqual(result.secrets, [
    { id: 's1', name: 'DEPLOY_TOKEN', delivery: 'env', description: 'CI deploy', availableAs: '$DEPLOY_TOKEN', present: true },
    { id: 's2', name: 'sa.json', delivery: 'file', availableAs: '/home/agent/.secrets/sa.json', present: true }
  ]);
  assert.equal(JSON.stringify(result).includes('never-shown'), false);
});

test('session_create passes secretIds in the text form, and the module never reads a file', async () => {
  const { withCredentialSelection } = await import('../../sessions/credentials.mjs');
  assert.deepEqual(withCredentialSelection({ secretIds: 's1, s2,s1' }).secretIds, ['s1', 's2']);
  assert.deepEqual(withCredentialSelection({ secretIds: 'none' }).secretIds, []);
  assert.equal('secretIds' in withCredentialSelection({ secretIds: '*' }), false);

  const source = fs.readFileSync(path.join(__dirname, '..', '..', 'sessions', 'secret-request.mjs'), 'utf8');
  assert.doesNotMatch(source, /readFile/);
  const server = fs.readFileSync(path.join(__dirname, '..', '..', 'sessions', 'server.mjs'), 'utf8');
  assert.match(server, /register\('secrets_list'/);
  assert.match(server, /register\('secret_request'/);
});

test('secrets_list preserves grant provenance and inheritance without values', async () => {
  const { listSecrets } = await load();
  const result = await listSecrets({ secrets: async () => ({ secrets: [{
    id: 'grant-a', name: 'DEPLOY_FILE', delivery: 'file', provenance: 'inherited', inheritToChildren: true,
    value: 'must-not-leave-backend'
  }] }) }, { HOME: '/home/agent' }, () => true);
  assert.equal(result.secrets[0].provenance, 'inherited');
  assert.equal(result.secrets[0].inheritToChildren, true);
  assert.equal(JSON.stringify(result).includes('must-not-leave-backend'), false);
});
