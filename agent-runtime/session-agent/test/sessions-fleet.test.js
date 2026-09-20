'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const env = () => ({
  AGENTHUB_CALLBACK_URL: 'http://backend/internal/sessions/session-1',
  AGENTHUB_CALLBACK_TOKEN: 'secret-token-never-leak',
  AGENTHUB_SESSION_ID: 'session-1'
});

const directory = [
  { id: 'session-1', title: 'Coordinator', description: 'Hands out tasks', phase: 'Running', self: true },
  { id: 'rev-1', title: 'Code Reviewer', description: 'Reviews MRs', phase: 'Running', self: false },
  { id: 'code-1', title: 'Coder', description: 'Implements tasks', phase: 'Running', self: false },
  { id: 'code-2', title: 'coder', description: 'Second coder', phase: 'Running', self: false }
];

test('fleet client calls project-agents, messages send, and inbox long-poll', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const calls = [];
  const client = new SessionsBackendClient(env(), async (url, init) => {
    calls.push({ url, init });
    if (url.endsWith('/project-agents')) return Response.json(directory);
    if (init.method === 'POST') return Response.json({ id: 'm-1', to: 'rev-1' });
    return Response.json({ messages: [{ id: 'm-2', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'ship it' }] });
  });

  const agents = await client.listProjectAgents();
  assert.equal(agents.length, 4);

  const sent = await client.sendAgentMessage('rev-1', 'please review MR 42');
  assert.equal(sent.to, 'rev-1');

  const inbox = await client.inbox(30);
  assert.equal(inbox.messages[0].fromTitle, 'Code Reviewer');

  assert.deepEqual(calls.map(c => [c.init.method, c.url]), [
    ['GET', 'http://backend/internal/sessions/session-1/project-agents'],
    ['POST', 'http://backend/internal/sessions/session-1/messages'],
    ['GET', 'http://backend/internal/sessions/session-1/messages?wait=30']
  ]);
  assert.deepEqual(JSON.parse(calls[1].init.body), { to: 'rev-1', body: 'please review MR 42' });
  for (const call of calls) {
    assert.equal(call.init.headers['X-Agent-Token'], 'secret-token-never-leak');
    assert.doesNotMatch(call.url, /secret/);
  }
});

test('inbox clamps waitSeconds to 0..60', async () => {
  const { SessionsBackendClient } = await import('../../sessions/client.mjs');
  const urls = [];
  const client = new SessionsBackendClient(env(), async url => {
    urls.push(url);
    return Response.json({ messages: [] });
  });

  await client.inbox();
  await client.inbox(-5);
  await client.inbox(999);
  await client.inbox('12');

  assert.deepEqual(urls.map(u => u.split('?wait=')[1]), ['0', '0', '60', '12']);
});

test('resolveAgentTarget matches ids and unique titles case-insensitively', async () => {
  const { resolveAgentTarget } = await import('../../sessions/resolve.mjs');
  const peers = directory.filter(a => !a.self);

  assert.equal(resolveAgentTarget(peers, 'rev-1'), 'rev-1');
  assert.equal(resolveAgentTarget(peers, 'Code Reviewer'), 'rev-1');
  assert.equal(resolveAgentTarget(peers, '  code reviewer '), 'rev-1');
});

test('resolveAgentTarget fails with candidates on ambiguity and misses', async () => {
  const { resolveAgentTarget } = await import('../../sessions/resolve.mjs');
  const peers = directory.filter(a => !a.self);

  try {
    resolveAgentTarget(peers, 'coder');
    assert.fail('expected ambiguity error');
  } catch (error) {
    assert.equal(error.code, 'agent_title_ambiguous');
    assert.deepEqual(error.candidates.map(c => c.id), ['code-1', 'code-2']);
  }

  try {
    resolveAgentTarget(peers, 'nobody');
    assert.fail('expected not-found error');
  } catch (error) {
    assert.equal(error.code, 'agent_not_found');
    assert.deepEqual(error.candidates.map(c => c.id), ['rev-1', 'code-1', 'code-2']);
  }
});
