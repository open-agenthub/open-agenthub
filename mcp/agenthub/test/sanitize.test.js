import test from 'node:test';
import assert from 'node:assert/strict';
import { sanitizeSession } from '../sanitize.mjs';

test('sanitizeSession omits mcpConfigJson and other secrets', () => {
  const raw = {
    id: 's1',
    title: 'child',
    owner: 'alice',
    mode: 'Autonomous',
    agent: 'Claude',
    authMode: 'Subscription',
    phase: 'Running',
    parentSessionId: 'parent',
    projectId: 'p1',
    prompt: 'do work',
    hasMcp: true,
    mcpConfigJson: '{"mcpServers":{"x":{"env":{"TOKEN":"secret"}}}}',
    podIp: '10.0.0.1',
    policy: { allow: ['*'] },
    image: 'custom:latest',
    repos: [{ url: 'https://example.com/r.git', branch: 'main', extra: 'drop' }],
    callbackToken: 'tok'
  };

  const safe = sanitizeSession(raw);
  assert.equal(safe.id, 's1');
  assert.equal(safe.prompt, 'do work');
  assert.equal(safe.hasMcp, true);
  assert.deepEqual(safe.repos, [{ url: 'https://example.com/r.git', branch: 'main' }]);
  assert.equal(safe.mcpConfigJson, undefined);
  assert.equal(safe.podIp, undefined);
  assert.equal(safe.policy, undefined);
  assert.equal(safe.callbackToken, undefined);
});

test('sanitizeSession maps lists and delete results', () => {
  assert.deepEqual(sanitizeSession({ deleted: true }), { deleted: true });
  const list = sanitizeSession([
    { id: 'a', phase: 'Running', mcpConfigJson: 'secret' }
  ]);
  assert.equal(list[0].mcpConfigJson, undefined);
});

test('sanitizeSession keeps the session url a caller hands to a person', () => {
  // The allowlist silently drops anything it does not name, so a create response without `url`
  // would leave an MCP caller with an id it cannot turn into a link.
  const safe = sanitizeSession({
    id: 's1', phase: 'Pending',
    url: 'https://agenthub.example.com/s/s1',
    systemPrompt: 'You review, you do not commit.',
    callbackToken: 'tok'
  });
  assert.equal(safe.url, 'https://agenthub.example.com/s/s1');
  assert.equal(safe.systemPrompt, 'You review, you do not commit.');
  assert.equal(safe.callbackToken, undefined);
});

test('sanitizeSession keeps the self-deletion setting and the deadline it yields', () => {
  const safe = sanitizeSession({
    id: 's1', phase: 'Running',
    autoDeleteAfterSeconds: 43200, autoDeleteFrom: 'lastActivity',
    expiresAt: '2026-10-11T00:00:00Z', lastActivityAt: '2026-10-10T12:00:00Z',
    callbackToken: 'tok'
  });
  assert.equal(safe.autoDeleteAfterSeconds, 43200);
  assert.equal(safe.autoDeleteFrom, 'lastActivity');
  assert.equal(safe.expiresAt, '2026-10-11T00:00:00Z');
  assert.equal(safe.lastActivityAt, '2026-10-10T12:00:00Z');
  assert.equal(safe.callbackToken, undefined);
});
