'use strict';

const fs = require('node:fs');
const path = require('node:path');

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
    const args = restoredResume ? ['resume', '--last'] : [];
    // `codex [OPTIONS] [PROMPT]` — the CLI's own help calls the positional the "optional user
    // prompt to start the session", and it keeps the TUI, so an API-created session is already
    // working when a person opens it. Left out on a resume: `resume` is a subcommand there and a
    // trailing prompt would both fail to parse and restate a task the thread already contains.
    const initialPrompt = env.AGENTHUB_PROMPT || '';
    if (initialPrompt && !restoredResume) args.push(initialPrompt);
    if (env.AGENTHUB_CODEX_DEVICE_AUTH === '1') {
      return { cmd: 'bash', args: [path.join(__dirname, 'device-login.sh'), ...args] };
    }
    return { cmd: 'codex', args };
  }

  const args = ['exec', ...EXEC_FLAGS];
  if (restoredResume) args.push('resume', '--last');
  args.push(env.AGENTHUB_PROMPT || '');
  return { cmd: 'codex', args };
}

function isResumeCommand(command) {
  if (!command || !Array.isArray(command.args)) return false;
  const args = command.args;
  if (command.cmd === 'bash') {
    return args.length === 3 && args[0] === path.join(__dirname, 'device-login.sh') &&
      args[1] === 'resume' && args[2] === '--last';
  }
  if (command.cmd !== 'codex') return false;
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

// CODEX_HOME is where the entrypoint restores the secret to; it defaults to ~/.codex, which is
// also what stateDir/authFilename name, so the override only matters when the image moved it.
function credentialPath(env) {
  return env.CODEX_HOME ? path.join(env.CODEX_HOME, 'auth.json') : null;
}

module.exports = {
  name: 'Codex', stateDir: '.codex', authFilename: 'auth.json',
  attachmentCapabilities: Object.freeze({
    nativeImages: false, localImagePaths: true, mcpImages: true }),
  buildCommand, isResumeCommand, isMissingResume, prepare,
  credentialPath,
  validCredential: require('./auth-watcher').validCredential
};
