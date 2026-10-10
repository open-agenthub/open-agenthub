import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');

test('agenthub MCP registers lifecycle and fleet tools', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  assert.match(server, /name:\s*'agenthub'/);
  for (const name of [
    'session_create', 'session_get', 'session_list', 'session_wait', 'session_delete',
    'agents_list', 'agent_send'
  ]) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  assert.match(server, /resolveAgentTarget/);
  assert.match(server, /AGENTHUB_URL|AgentHubClient/);
  assert.match(server, /mode:\s*z\.[\s\S]*?\.default\('Interactive'\)/);
  assert.match(server, /sanitizeSession/);
});

test('agenthub MCP exposes transcript polling and the caller system prompt', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  assert.match(server, /register\('session_transcript'/);
  // The page is not a session record; sanitizeSession's allowlist would strip all of it.
  assert.doesNotMatch(server, /sanitizeSession\(await client\.transcript/);
  assert.match(server, /offset: z\.number\(\)\.int\(\)\.min\(0\)\.optional\(\)/);
  assert.match(server, /systemPrompt: z\.string\(\)\.max\(20_000\)\.optional\(\)/);
});

test('agenthub MCP takes the self-deletion deadline as text and converts it before the HTTP call', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  assert.match(server, /autoDeleteAfter: z\.string\(\)\.max\(16\)\.optional\(\)/);
  assert.match(server, /autoDeleteFrom: z\.enum\(\['start', 'lastActivity'\]\)\.optional\(\)/);
  assert.match(server, /client\.create\(withExpiry\(body\)\)/);
  // The parser's error is a stable code, not the generic operation_failed.
  assert.match(server, /'autodelete_invalid_duration'/);
});
