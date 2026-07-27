'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { writeCliConfig } = require('../../cursor/cli-config');

test('cli-config maps allowedTools and MCP tools into permissions.allow for automation', () => {
  const cfg = writeCliConfig({
    mode: 'autonomous',
    allowedTools: ['Shell(git status)', 'Read(**)'],
    allowedMcpTools: ['docs:search'],
    allowedCommands: []
  });
  const parsed = JSON.parse(cfg);
  assert.equal(parsed.version, 1);
  // Official schema: approvalMode is top-level (not under permissions).
  assert.equal(parsed.approvalMode, 'allowlist');
  assert.equal(parsed.sandbox.mode, 'disabled');
  assert.ok(parsed.permissions.allow.includes('Shell(git status)'));
  assert.ok(parsed.permissions.allow.includes('Read(**)'));
  assert.ok(parsed.permissions.allow.includes('Mcp(docs:search)'));
  assert.deepEqual(parsed.permissions.deny, []);
});

test('cli-config maps allowedCommands into Shell permissions', () => {
  const parsed = JSON.parse(writeCliConfig({
    mode: 'scheduled',
    allowedTools: [],
    allowedMcpTools: [],
    allowedCommands: ['git status', 'npm test']
  }));
  assert.equal(parsed.approvalMode, 'allowlist');
  assert.ok(parsed.permissions.allow.includes('Shell(git status)'));
  assert.ok(parsed.permissions.allow.includes('Shell(npm test)'));
});

test('cli-config keeps interactive mode without force-style unrestricted approval', () => {
  const parsed = JSON.parse(writeCliConfig({
    mode: 'interactive',
    allowedTools: ['Read(**)'],
    allowedMcpTools: [],
    allowedCommands: []
  }));
  assert.equal(parsed.version, 1);
  assert.notEqual(parsed.approvalMode, 'unrestricted');
  assert.ok(parsed.permissions.allow.includes('Read(**)'));
});

test('cli-config remains default-deny when automation policy lists are empty', () => {
  const parsed = JSON.parse(writeCliConfig({
    mode: 'autonomous',
    allowedTools: [],
    allowedMcpTools: [],
    allowedCommands: []
  }));
  assert.equal(parsed.approvalMode, 'allowlist');
  assert.deepEqual(parsed.permissions.allow, []);
  assert.deepEqual(parsed.permissions.deny, []);
  assert.equal(parsed.sandbox.mode, 'disabled');
});

test('cli-config rejects unsafe MCP and shell policy entries', () => {
  assert.throws(() => writeCliConfig({
    mode: 'autonomous',
    allowedTools: [],
    allowedMcpTools: ['not valid'],
    allowedCommands: []
  }), /invalid.*mcp/i);
  assert.throws(() => writeCliConfig({
    mode: 'autonomous',
    allowedTools: [],
    allowedMcpTools: [],
    allowedCommands: ['rm -rf /; reboot']
  }), /unsafe|shell|command/i);
});
