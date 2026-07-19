'use strict';

const fs = require('node:fs');

function prepare(env) {
  if (env.AGENTHUB_STATE_RESTORED === undefined) {
    env.AGENTHUB_STATE_RESTORED = fs.existsSync('/tmp/.state-restored') ? '1' : '0';
  }
}

function buildCommand(env, allowResume) {
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const prompt = env.AGENTHUB_PROMPT || '';
  const allowed = (env.AGENTHUB_ALLOWED_TOOLS || '').split(',')
    .map(value => value.trim()).filter(Boolean);
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
