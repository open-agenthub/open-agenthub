'use strict';

const fs = require('node:fs');
const path = require('node:path');

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
    // --trust first, and before the positional: `[prompt...]` is variadic and swallows every
    // remaining argument. Without it the TUI stops on its workspace-trust question, so a session
    // created with a task would sit on a dialog instead of working — the non-interactive branch
    // below has passed it for the same reason since it was written.
    const args = ['--trust'];
    if (restoredResume) args.push('--resume', chatId);
    // `agent [options] [prompt...]` — the CLI documents the positional as the agent's initial
    // prompt and stays interactive with it, so a session created by an API arrives with its task
    // under way. Not on a resume, where the chat already holds it.
    if (prompt && !restoredResume) args.push(prompt);
    // Subscription login must run in the session PTY, then exec into the TUI
    // (entrypoint only sets the flag; mirrors Codex device-login.sh).
    if (env.AGENTHUB_CURSOR_LOGIN === '1') {
      return { cmd: 'bash', args: [path.join(__dirname, 'login.sh'), ...args] };
    }
    return { cmd: 'agent', args };
  }

  const args = ['-p', '--force', '--trust'];
  if (env.AGENTHUB_HAS_MCP === '1') args.push('--approve-mcps');
  if (restoredResume) args.push('--resume', chatId);
  args.push(prompt);
  return { cmd: 'agent', args };
}

function isResumeCommand(command) {
  if (!command || !Array.isArray(command.args)) return false;
  const args = command.args;
  // Both forms are matched by looking for --resume and its value rather than by argument count:
  // the interactive command now also carries --trust and may carry an initial prompt, and a
  // position-counting check would call a resumed session fresh and silently restart its work.
  if (command.cmd !== 'bash' && command.cmd !== 'agent') return false;
  if (command.cmd === 'bash' && args[0] !== path.join(__dirname, 'login.sh')) return false;
  const index = args.indexOf('--resume');
  return index >= 0 && typeof args[index + 1] === 'string' &&
    args[index + 1].length > 0;
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
  attachmentCapabilities: Object.freeze({
    nativeImages: false, localImagePaths: true, mcpImages: true }),
  buildCommand, isResumeCommand, isMissingResume, prepare
};
