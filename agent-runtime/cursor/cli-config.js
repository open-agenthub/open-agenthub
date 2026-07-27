'use strict';

const MCP_TOOL = /^[A-Za-z0-9_-]+:(?:[A-Za-z0-9_-]+|\*)$/;
const SAFE_COMMAND = /^[A-Za-z0-9_./:@%+=-]+(?: [A-Za-z0-9_./:@%+=-]+)*$/;
const ASSIGNMENT = /^[A-Za-z_][A-Za-z0-9_]*=/;

function parseList(value, label) {
  if (value === undefined || value === null || value === '') return [];
  let values = value;
  if (typeof value === 'string') {
    try { values = JSON.parse(value); } catch {
      throw new Error('Invalid Cursor ' + label + ' policy JSON.');
    }
  }
  if (!Array.isArray(values) || values.some(item => typeof item !== 'string')) {
    throw new Error('Invalid Cursor ' + label + ' policy JSON.');
  }
  return values.map(item => item.trim()).filter(Boolean);
}

function allowFromPolicy(policy) {
  const allow = [];
  for (const tool of parseList(policy.allowedTools, 'tools')) allow.push(tool);
  for (const command of parseList(policy.allowedCommands, 'shell')) {
    const tokens = command.split(' ');
    if (command.includes(',') || !SAFE_COMMAND.test(command) || tokens.some(token => ASSIGNMENT.test(token))) {
      throw new Error('Unsafe Cursor shell policy entry: ' + command);
    }
    allow.push('Shell(' + command + ')');
  }
  for (const tool of parseList(policy.allowedMcpTools, 'MCP')) {
    if (!MCP_TOOL.test(tool)) throw new Error('Invalid Cursor MCP policy entry: ' + tool);
    allow.push('Mcp(' + tool + ')');
  }
  return [...new Set(allow)];
}

function writeCliConfig(policy = {}) {
  const resolved = {
    mode: String(policy.mode || process.env.AGENTHUB_MODE || 'interactive').toLowerCase(),
    allowedTools: policy.allowedTools !== undefined ? policy.allowedTools :
      process.env.AGENTHUB_ALLOWED_TOOLS,
    allowedMcpTools: policy.allowedMcpTools !== undefined ? policy.allowedMcpTools :
      process.env.AGENTHUB_ALLOWED_MCP_TOOLS,
    allowedCommands: policy.allowedCommands !== undefined ? policy.allowedCommands :
      process.env.AGENTHUB_ALLOWED_COMMANDS
  };
  const allow = allowFromPolicy(resolved);
  // Official cli-config.json schema (cursor.com/docs/cli/reference/configuration):
  // version, permissions.allow/deny, approvalMode (top-level), sandbox.mode.
  return JSON.stringify({
    version: 1,
    editor: { vimMode: false },
    approvalMode: 'allowlist',
    permissions: { allow, deny: [] },
    sandbox: { mode: 'disabled' }
  }, null, 2) + '\n';
}

if (require.main === module) {
  try {
    process.stdout.write(writeCliConfig());
  } catch (error) {
    console.error('[cursor-cli-config] Policy rejected: ' + error.message);
    process.exit(1);
  }
}

module.exports = { writeCliConfig };
