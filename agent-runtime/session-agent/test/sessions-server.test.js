'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('agenthub_sessions registers lifecycle and fleet tools', () => {
  const server = fs.readFileSync(
    path.join(__dirname, '..', '..', 'sessions', 'server.mjs'),
    'utf8'
  );
  assert.match(server, /name:\s*'agenthub_sessions'/);
  for (const name of [
    'session_create', 'session_get', 'session_list', 'session_wait', 'session_delete',
    'agents_list', 'agent_send', 'agent_inbox'
  ]) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  // A child a person can watch and answer is the safe default; Autonomous auto-approves.
  assert.match(server, /mode:\s*z\.[\s\S]*?\.default\('Interactive'\)/);
  assert.match(server, /mcpServerIds:\s*z\.array\(/);
  assert.match(server, /sanitizeSession/);
  // Title resolution goes through the shared resolver and never targets the sender itself.
  assert.match(server, /resolveAgentTarget/);
  assert.match(server, /filter\(a => !a\?\.self\)/);
});

test('agenthub_sessions takes the credential selection as text and converts it before the spawn', () => {
  const server = fs.readFileSync(
    path.join(__dirname, '..', '..', 'sessions', 'server.mjs'),
    'utf8'
  );
  // Strings, not arrays: an already-connected client sends a new parameter as text.
  assert.match(server, /credentialId: z\.string\(\)\.max\(64\)\.optional\(\)/);
  assert.match(server, /gitPatIds: z\.string\(\)\.max\(4096\)\.optional\(\)/);
  assert.match(server, /client\.create\(withCredentialSelection\(body\)\)/);
});

test('sessions credentials helper turns the text parameters into the spawn body fields', async () => {
  const { parseIdList, withCredentialSelection } = await import('../../sessions/credentials.mjs');
  assert.deepEqual(parseIdList('a, b,,a'), ['a', 'b']);
  assert.equal(parseIdList(''), undefined);
  assert.equal(parseIdList('*'), undefined);
  assert.deepEqual(parseIdList('none'), []);
  assert.deepEqual(
    withCredentialSelection({ title: 'x', credentialId: ' acct ', gitPatIds: 'p1,p2' }),
    { title: 'x', credentialId: 'acct', gitPatIds: ['p1', 'p2'] });
  assert.deepEqual(withCredentialSelection({ title: 'x', credentialId: '', gitPatIds: '' }), { title: 'x' });
});
