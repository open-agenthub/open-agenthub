'use strict';

const fs = require('node:fs');

const EXEC_FLAGS = ['--sandbox', 'workspace-write', '--json', '--dangerously-bypass-hook-trust'];

function prepare(env) {
  if (env.AGENTHUB_STATE_RESTORED === undefined) {
    env.AGENTHUB_STATE_RESTORED = fs.existsSync('/tmp/.state-restored') ? '1' : '0';
  }

  const apiKey = env.CODEX_API_KEY;
  delete env.CODEX_API_KEY;
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  if (apiKey && mode !== 'interactive') return { childEnv: { CODEX_API_KEY: apiKey } };
  return undefined;
}

function buildCommand(env, allowResume) {
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const restoredResume = allowResume && env.AGENTHUB_RESUME === '1' &&
    env.AGENTHUB_STATE_RESTORED === '1';
  if (mode === 'interactive') {
    return { cmd: 'codex', args: restoredResume ? ['resume', '--last'] : [] };
  }

  const args = ['exec', ...EXEC_FLAGS];
  if (restoredResume) args.push('resume', '--last');
  args.push(env.AGENTHUB_PROMPT || '');
  return { cmd: 'codex', args };
}

function isResumeCommand(command) {
  if (!command || command.cmd !== 'codex' || !Array.isArray(command.args)) return false;
  const args = command.args;
  if (args.length === 2) return args[0] === 'resume' && args[1] === '--last';
  return args.length === 8 && args[0] === 'exec' && args[1] === '--sandbox' &&
    args[2] === 'workspace-write' && args[3] === '--json' &&
    args[4] === '--dangerously-bypass-hook-trust' && args[5] === 'resume' && args[6] === '--last';
}

function isMissingResume(output, exitCode) {
  if (exitCode === 0 || typeof output !== 'string') return false;
  return /no saved (?:session|conversation|thread) found(?: to resume)?/i.test(output) ||
    /no session found with id\b/i.test(output);
}

module.exports = {
  name: 'Codex', stateDir: '.codex', authFilename: 'auth.json',
  buildCommand, isResumeCommand, isMissingResume, prepare
};
