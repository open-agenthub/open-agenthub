import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');

test('agenthub MCP registers create/get/list/wait/delete tools', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  assert.match(server, /name:\s*'agenthub'/);
  for (const name of [
    'session_create', 'session_get', 'session_list', 'session_wait', 'session_delete'
  ]) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  assert.match(server, /AGENTHUB_URL|AgentHubClient/);
  assert.match(server, /mode:\s*body\.mode\s*\?\?\s*'Autonomous'|mode:\s*z\.[\s\S]*?\.default\('Autonomous'\)/);
});
