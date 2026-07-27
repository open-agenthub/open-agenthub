'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('agenthub_sessions registers create/get/list/wait/delete tools', () => {
  const server = fs.readFileSync(
    path.join(__dirname, '..', '..', 'sessions', 'server.mjs'),
    'utf8'
  );
  assert.match(server, /name:\s*'agenthub_sessions'/);
  for (const name of [
    'session_create', 'session_get', 'session_list', 'session_wait', 'session_delete'
  ]) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  assert.match(server, /mode:\s*body\.mode\s*\?\?\s*'Autonomous'|mode:\s*z\.[\s\S]*?\.default\('Autonomous'\)/);
});
