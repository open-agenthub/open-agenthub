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

test('agenthub MCP registers the sharing tools without the session allowlist', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  for (const name of ['session_share', 'session_unshare', 'session_share_link', 'session_shares']) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  // Grants and links are not session records; sanitizeSession's allowlist would empty them.
  assert.doesNotMatch(server, /sanitizeSession\(await client\.(listShares|shareWithUser|unshareUser|createShareLink)/);
  // Errors go through the sharing vocabulary (license_required, session_not_found, …).
  assert.match(server, /sharingErrorCode/);
  assert.match(server, /role: roleSchema/);
});

test('agenthub MCP exposes transcript polling and the caller system prompt', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  assert.match(server, /register\('session_transcript'/);
  // The page is not a session record; sanitizeSession's allowlist would strip all of it.
  assert.doesNotMatch(server, /sanitizeSession\(await client\.transcript/);
  assert.match(server, /offset: z\.number\(\)\.int\(\)\.min\(0\)\.optional\(\)/);
  assert.match(server, /systemPrompt: z\.string\(\)\.max\(20_000\)\.optional\(\)/);
});
