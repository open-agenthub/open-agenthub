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
    'session_convert', 'agents_list', 'agent_send', 'credentials_list'
  ]) {
    assert.match(server, new RegExp(`register\\('${name}'`));
  }
  assert.match(server, /resolveAgentTarget/);
  assert.match(server, /AGENTHUB_URL|AgentHubClient/);
  assert.match(server, /mode:\s*z\.[\s\S]*?\.default\('Interactive'\)/);
  assert.match(server, /sanitizeSession/);
  // agent_send carries the priority flags as real booleans (zod), unlike the remote MCP's strings.
  assert.match(server, /priority: z\.boolean\(\)\.optional\(\)/);
  assert.match(server, /interrupt: z\.boolean\(\)\.optional\(\)/);
  assert.match(server, /sendAgentMessage\(targetId, message, \{ priority, interrupt \}\)/);
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

test('agenthub MCP converts by sessionId with the same flags as the REST body', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  const tool = server.slice(server.indexOf("register('session_convert'"));
  assert.match(tool, /sessionId: z\.string\(\)\.min\(1\)\.max\(128\)/);
  assert.match(tool, /uiMode: z\.enum\(\['terminal', 'chat'\]\)\.optional\(\)/);
  assert.match(tool, /autoApprove: z\.boolean\(\)\.optional\(\)/);
  assert.match(tool, /resume: z\.boolean\(\)\.optional\(\)/);
  assert.match(tool, /client\.convert\(sessionId, body\)/);
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
  assert.match(server, /client\.create\(withCredentialSelection\(withExpiry\(body\)\)\)/);
  // The parser's error is a stable code, not the generic operation_failed.
  const errors = fs.readFileSync(path.join(root, 'errors.mjs'), 'utf8');
  assert.match(errors, /'autodelete_invalid_duration'/);
});

test('agenthub MCP takes the credential selection as text and lists credentials unsanitized', () => {
  const server = fs.readFileSync(path.join(root, 'server.mjs'), 'utf8');
  // Strings, not arrays: an already-connected client sends a new parameter as text.
  assert.match(server, /credentialId: z\.string\(\)\.max\(64\)\.optional\(\)/);
  assert.match(server, /gitPatIds: z\.string\(\)\.max\(4096\)\.optional\(\)/);
  assert.match(server, /client\.create\(withCredentialSelection\(withExpiry\(body\)\)\)/);
  // The listing is not a session record; the allowlist would empty it.
  assert.doesNotMatch(server, /sanitizeSession\(await client\.credentials/);
});
