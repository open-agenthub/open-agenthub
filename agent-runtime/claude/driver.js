'use strict';

const fs = require('node:fs');

function prepare(env) {
  const apiKey = env.ANTHROPIC_API_KEY;
  delete env.ANTHROPIC_API_KEY;
  if (env.AGENTHUB_STATE_RESTORED === undefined) {
    env.AGENTHUB_STATE_RESTORED = fs.existsSync('/tmp/.state-restored') ? '1' : '0';
  }
  if (apiKey !== undefined) {
    return { childEnv: { ANTHROPIC_API_KEY: apiKey } };
  }
}

function structuredPolicyList(env, name, label) {
  const raw = env[name];
  if (!raw) return [];
  let values;
  try {
    values = JSON.parse(raw);
  } catch {
    throw new Error(`Invalid Claude ${label} policy JSON.`);
  }
  if (!Array.isArray(values) || values.some(value => typeof value !== 'string')) {
    throw new Error(`Invalid Claude ${label} policy JSON.`);
  }
  return values.map(value => value.trim()).filter(Boolean);
}

function builtInPolicyList(env) {
  const raw = env.AGENTHUB_ALLOWED_TOOLS;
  if (!raw) return [];
  let values;
  try {
    values = JSON.parse(raw);
  } catch {
    // Legacy pods supplied one or more comma-separated entries. Only a single
    // entry can be preserved without reintroducing delimiter injection.
    values = [raw];
  }
  if (!Array.isArray(values) || values.some(value => typeof value !== 'string')) {
    throw new Error('Invalid Claude built-in policy JSON.');
  }
  return values.map(value => value.trim()).filter(Boolean).map(rule => {
    // Native Claude tool selectors are a tool name with an optional, balanced
    // parenthesized pattern. Commas and shell metacharacters are not selectors.
    const nativeSelector = /^[A-Za-z][A-Za-z0-9_-]*(?:\([A-Za-z0-9_./:@%+=* -]+\))?$/;
    if (!nativeSelector.test(rule)) {
      throw new Error(`Invalid Claude built-in policy entry: ${rule}`);
    }
    return rule;
  });
}

function nativeAllowedTools(env) {
  const builtIns = builtInPolicyList(env);
  const mcpRules = structuredPolicyList(env, 'AGENTHUB_ALLOWED_MCP_TOOLS', 'MCP')
    .map(rule => {
      if (!/^mcp__[A-Za-z0-9_-]+__(?:[A-Za-z0-9_-]+|\*)$/.test(rule)) {
        throw new Error(`Invalid Claude MCP policy entry: ${rule}`);
      }
      return rule;
    });
  const shellRules = structuredPolicyList(env, 'AGENTHUB_ALLOWED_COMMANDS', 'shell')
    .map(command => {
      const tokens = command.split(' ');
      const safeCharacters = /^[A-Za-z0-9_./:@%+=-]+(?: [A-Za-z0-9_./:@%+=-]+)*$/;
      const assignment = /^[A-Za-z_][A-Za-z0-9_]*=/;
      if (command.includes(',') || !safeCharacters.test(command)
          || tokens.some(token => assignment.test(token))) {
        throw new Error(`Unsafe Claude shell policy entry: ${command}`);
      }
      return `Bash(${command})`;
    });
  return [...new Set([...builtIns, ...mcpRules, ...shellRules])];
}

function buildCommand(env, allowResume) {
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const prompt = env.AGENTHUB_PROMPT || '';
  const sessionId = env.AGENTHUB_CLAUDE_SESSION_ID || '';
  const args = [];

  if (env.AGENTHUB_HAS_MCP === '1') args.push('--mcp-config', '/secrets/mcp/mcp.json');

  if (allowResume && env.AGENTHUB_RESUME === '1' && sessionId &&
      env.AGENTHUB_STATE_RESTORED === '1') {
    args.push('--resume', sessionId);
  } else if (sessionId) {
    args.push('--session-id', sessionId);
  }

  if (mode !== 'interactive') {
    args.push('-p', prompt, '--permission-mode', 'acceptEdits');
    const allowed = nativeAllowedTools(env);
    if (allowed.length) args.push('--allowedTools', allowed.join(','));
  }

  return { cmd: 'claude', args };
}

function isResumeCommand(command) {
  return command.args.includes('--resume');
}

function isMissingResume(output, exitCode, elapsedMs) {
  return exitCode !== 0 && (output.includes('No conversation found') || elapsedMs < 10_000);
}

module.exports = {
  name: 'Claude',
  stateDir: '.claude',
  authFilename: '.credentials.json',
  buildCommand,
  isResumeCommand,
  isMissingResume,
  prepare
};
