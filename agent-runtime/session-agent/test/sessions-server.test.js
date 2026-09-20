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
  assert.match(server, /mode:\s*body\.mode\s*\?\?\s*'Autonomous'|mode:\s*z\.[\s\S]*?\.default\('Autonomous'\)/);
  assert.match(server, /sanitizeSession/);
  // Title resolution goes through the shared resolver and never targets the sender itself.
  assert.match(server, /resolveAgentTarget/);
  assert.match(server, /filter\(a => !a\?\.self\)/);
});
