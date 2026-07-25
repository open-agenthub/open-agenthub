'use strict';

const fs = require('node:fs');

function prepare(env) {
  if (env.AGENTHUB_STATE_RESTORED === undefined) {
    env.AGENTHUB_STATE_RESTORED = fs.existsSync('/tmp/.state-restored') ? '1' : '0';
  }

  // CURSOR_API_KEY is the supported auth path for all modes (no `agent login --with-api-key`).
  // Scrub it from the long-lived server/shell env and pass it only to the agent child.
  const apiKey = env.CURSOR_API_KEY;
  delete env.CURSOR_API_KEY;
  if (apiKey) return { childEnv: { CURSOR_API_KEY: apiKey } };
  return undefined;
}

function sessionId(env) {
  return env.AGENTHUB_CURSOR_SESSION_ID || env.AGENTHUB_CLAUDE_SESSION_ID || '';
}

function buildCommand(env, allowResume) {
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const prompt = env.AGENTHUB_PROMPT || '';
  const chatId = sessionId(env);
  const restoredResume = allowResume && env.AGENTHUB_RESUME === '1' &&
    env.AGENTHUB_STATE_RESTORED === '1' && chatId;

  if (mode === 'interactive') {
    if (restoredResume) return { cmd: 'agent', args: ['--resume', chatId] };
    return { cmd: 'agent', args: [] };
  }

  const args = ['-p', '--force', '--trust'];
  if (env.AGENTHUB_HAS_MCP === '1') args.push('--approve-mcps');
  if (restoredResume) args.push('--resume', chatId);
  args.push(prompt);
  return { cmd: 'agent', args };
}

function isResumeCommand(command) {
  if (!command || command.cmd !== 'agent' || !Array.isArray(command.args)) return false;
  const index = command.args.indexOf('--resume');
  return index >= 0 && typeof command.args[index + 1] === 'string' &&
    command.args[index + 1].length > 0;
}

function isMissingResume(output, exitCode) {
  if (exitCode === 0 || typeof output !== 'string') return false;
  return /no conversation found/i.test(output) ||
    /chat not found/i.test(output) ||
    /no (?:saved )?(?:session|conversation|chat) (?:found|with id)/i.test(output);
}

module.exports = {
  // authFilename: CLI file store (AGENT_CLI_CREDENTIAL_STORE=file, domain "cursor")
  // writes auth.json — confirmed from CLI package 2026.07.23-e383d2b source.
  name: 'Cursor', stateDir: '.cursor', authFilename: 'auth.json',
  buildCommand, isResumeCommand, isMissingResume, prepare
};
