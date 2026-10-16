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
    'session_convert', 'agents_list', 'agent_send', 'agent_inbox', 'account_status', 'account_switch'
  ]) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  // The account tools take an optional session id: none means this session itself, which may
  // switch its own account (docs/account-limits.md); anything else goes through the peer rule.
  const status = server.slice(server.indexOf("register('account_status'"), server.indexOf("register('account_switch'"));
  assert.match(status, /sessionId: z\.string\(\)\.min\(1\)\.max\(128\)\.optional\(\)/);
  assert.doesNotMatch(status, /sanitizeSession\(await client\.accountStatus/);
  const switching = server.slice(server.indexOf("register('account_switch'"));
  assert.match(switching, /credentialId: z\.string\(\)\.min\(1\)\.max\(64\)/);
  assert.match(switching, /client\.switchAccount\(sessionId, credentialId\)/);
  assert.match(server, /accountFailover: z\.enum\(\['auto', 'off'\]\)\.optional\(\)/);
  // Conversion goes through the peer route, so the descendant rule of session_get applies.
  const convert = server.slice(server.indexOf("register('session_convert'"));
  assert.match(convert, /sessionId: z\.string\(\)\.min\(1\)\.max\(128\)/);
  assert.match(convert, /uiMode: z\.enum\(\['terminal', 'chat'\]\)\.optional\(\)/);
  assert.match(convert, /client\.convert\(sessionId, body\)/);
  // A child a person can watch and answer is the safe default; Autonomous auto-approves.
  assert.match(server, /mode:\s*z\.[\s\S]*?\.default\('Interactive'\)/);
  assert.match(server, /mcpServerIds:\s*z\.array\(/);
  // Fields zod would otherwise strip before the call reaches the hub: without providerId a child
  // can clone but not push, and without autoApprove an agent cannot start an unattended child.
  assert.match(server, /providerId:\s*z\.string\(\)/);
  assert.match(server, /autoApprove:\s*z\.boolean\(\)/);
  assert.match(server, /sanitizeSession/);
  // Title resolution goes through the shared resolver and never targets the sender itself.
  assert.match(server, /resolveAgentTarget/);
  assert.match(server, /filter\(a => !a\?\.self\)/);
  // agent_send carries the priority flags as real booleans (zod), unlike the remote MCP's strings.
  assert.match(server, /priority: z\.boolean\(\)\.optional\(\)/);
  assert.match(server, /interrupt: z\.boolean\(\)\.optional\(\)/);
  assert.match(server, /sendAgentMessage\(targetId, message, \{ priority, interrupt \}\)/);
});

test('agenthub_sessions takes the credential selection as text and converts it before the spawn', () => {
  const server = fs.readFileSync(
    path.join(__dirname, '..', '..', 'sessions', 'server.mjs'),
    'utf8'
  );
  // Strings, not arrays: an already-connected client sends a new parameter as text.
  assert.match(server, /credentialId: z\.string\(\)\.max\(64\)\.optional\(\)/);
  assert.match(server, /gitPatIds: z\.string\(\)\.max\(4096\)\.optional\(\)/);
  assert.match(server, /client\.create\(withCredentialSelection\(withExpiry\(body\)\)\)/);
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
