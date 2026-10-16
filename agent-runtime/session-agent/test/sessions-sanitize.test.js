'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

test('sanitizeSession omits mcpConfigJson and other secrets', async () => {
  const { sanitizeSession } = await import('../../sessions/sanitize.mjs');
  const raw = {
    id: 's1',
    title: 'child',
    description: 'Implements tasks handed over by the reviewer.',
    owner: 'alice',
    mode: 'Autonomous',
    agent: 'Claude',
    authMode: 'Subscription',
    phase: 'Running',
    parentSessionId: 'parent',
    projectId: 'p1',
    prompt: 'do work',
    schedule: null,
    questionPending: false,
    createdAt: '2026-07-28T00:00:00Z',
    hasMcp: true,
    mcpConfigJson: '{"mcpServers":{"x":{"env":{"TOKEN":"secret"}}}}',
    podIp: '10.0.0.1',
    policy: { allow: ['*'] },
    image: 'custom:latest',
    cpu: '2',
    memory: '4Gi',
    runAsRoot: true,
    browser: { state: 'running', vncPassword: 'nope' },
    repos: [{ url: 'https://example.com/r.git', branch: 'main', providerId: 'github', extra: 'drop' }],
    autoApprove: true,
    callbackToken: 'tok'
  };

  const safe = sanitizeSession(raw);
  assert.equal(safe.id, 's1');
  assert.equal(safe.description, 'Implements tasks handed over by the reviewer.');
  assert.equal(safe.prompt, 'do work');
  assert.equal(safe.hasMcp, true);
  assert.deepEqual(safe.repos, [{ url: 'https://example.com/r.git', branch: 'main', providerId: 'github' }]);
  assert.equal(safe.autoApprove, true);
  assert.equal(safe.mcpConfigJson, undefined);
  assert.equal(safe.podIp, undefined);
  assert.equal(safe.policy, undefined);
  assert.equal(safe.image, undefined);
  assert.equal(safe.callbackToken, undefined);
  assert.equal(safe.browser, undefined);
  assert.equal(safe.cpu, undefined);
});

test('sanitizeSession maps lists and delete results', async () => {
  const { sanitizeSession } = await import('../../sessions/sanitize.mjs');
  assert.deepEqual(sanitizeSession({ deleted: true }), { deleted: true });
  const list = sanitizeSession([
    { id: 'a', phase: 'Running', mcpConfigJson: 'secret' },
    { id: 'b', phase: 'Failed', mcpConfigJson: 'secret' }
  ]);
  assert.equal(list.length, 2);
  assert.equal(list[0].mcpConfigJson, undefined);
  assert.equal(list[1].id, 'b');
});

test('sanitizeSession keeps the self-deletion setting and the deadline it yields', async () => {
  const { sanitizeSession } = await import('../../sessions/sanitize.mjs');
  const safe = sanitizeSession({
    id: 's1', phase: 'Running',
    autoDeleteAfterSeconds: 43200, autoDeleteFrom: 'start',
    expiresAt: '2026-10-11T00:00:00Z', lastActivityAt: '2026-10-10T12:00:00Z',
    callbackToken: 'tok'
  });
  assert.equal(safe.autoDeleteAfterSeconds, 43200);
  assert.equal(safe.autoDeleteFrom, 'start');
  assert.equal(safe.expiresAt, '2026-10-11T00:00:00Z');
  assert.equal(safe.callbackToken, undefined);
});
