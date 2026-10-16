import test from 'node:test';
import assert from 'node:assert/strict';

const env = () => ({
  AGENTHUB_URL: 'https://hub.example.com',
  AGENTHUB_TOKEN: 'oah_secret_never_leak'
});

const sessions = [
  { id: 'rev-1', title: 'Code Reviewer', description: 'Reviews MRs', phase: 'Running', projectId: 'proj-a' },
  { id: 'code-1', title: 'Coder', description: 'Implements tasks', phase: 'Running', projectId: 'proj-a' },
  { id: 'other-1', title: 'Coder', description: 'Different project', phase: 'Running', projectId: 'proj-b' }
];

test('listAgents filters by projectId on top of the remote session list', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const client = new AgentHubClient(env(), async () => Response.json(sessions));

  assert.equal((await client.listAgents()).length, 3);
  assert.deepEqual((await client.listAgents('proj-a')).map(s => s.id), ['rev-1', 'code-1']);
});

test('sendAgentMessage posts to the remote messages endpoint with the bearer token', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const calls = [];
  const client = new AgentHubClient(env(), async (url, init) => {
    calls.push({ url, init });
    return Response.json({ id: 'm-1', to: 'rev-1' });
  });

  const result = await client.sendAgentMessage('rev-1', 'please review MR 42');

  assert.equal(result.to, 'rev-1');
  assert.equal(calls[0].url, 'https://hub.example.com/api/remote/sessions/rev-1/messages');
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers.Authorization, 'Bearer oah_secret_never_leak');
  assert.deepEqual(JSON.parse(calls[0].init.body), { body: 'please review MR 42' });
});

test('sendAgentMessage passes priority and interrupt only when set', async () => {
  const { AgentHubClient } = await import('../client.mjs');
  const bodies = [];
  const client = new AgentHubClient(env(), async (url, init) => {
    bodies.push(JSON.parse(init.body));
    return Response.json({ id: 'm-1', to: 'rev-1', deliveredVia: 'injected' });
  });

  await client.sendAgentMessage('rev-1', 'now', { priority: true });
  const result = await client.sendAgentMessage('rev-1', 'stop', { priority: false, interrupt: true });

  assert.deepEqual(bodies, [
    { body: 'now', priority: true },
    { body: 'stop', priority: false, interrupt: true }
  ]);
  assert.equal(result.deliveredVia, 'injected');
});

test('resolveAgentTarget disambiguates only within the requested project scope', async () => {
  const { resolveAgentTarget } = await import('../resolve.mjs');

  // Across all projects the title "Coder" is ambiguous …
  try {
    resolveAgentTarget(sessions, 'Coder');
    assert.fail('expected ambiguity error');
  } catch (error) {
    assert.equal(error.code, 'agent_title_ambiguous');
    assert.deepEqual(error.candidates.map(c => c.id), ['code-1', 'other-1']);
  }

  // … but unique inside proj-a.
  const scoped = sessions.filter(s => s.projectId === 'proj-a');
  assert.equal(resolveAgentTarget(scoped, 'Coder'), 'code-1');
  assert.equal(resolveAgentTarget(scoped, 'rev-1'), 'rev-1');
});
