'use strict';

const fs = require('node:fs');
const path = require('node:path');

// Since 0.160 the TUI turns its mouse capture off again but stays on the alternate screen with
// alternate-scroll set, so the web terminal turns the wheel into arrow keys: Codex reads those as
// prompt history and nothing scrolls. Inline mode writes into the normal scrollback, which
// xterm.js scrolls natively whatever the CLI does with the mouse.
// --no-daemon: by default the TUI copies a ~400 MB app-server package into
// ~/.codex/packages and talks to it as a background daemon. A pod runs exactly one TUI, so the
// daemon buys nothing, and a state archive that caught the package half-installed restored a
// `current` link without its binary — every later start then failed with "daemon executable
// not found". In-process, the TUI needs neither the package nor the daemon's pid bookkeeping.
const TUI_FLAGS = ['--no-alt-screen', '--no-daemon'];
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
    const args = [...TUI_FLAGS];
    if (restoredResume) args.push('resume', '--last');
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
    return args[0] === path.join(__dirname, 'device-login.sh') && isTuiResume(args.slice(1));
  }
  if (command.cmd !== 'codex') return false;
  if (args[0] !== 'exec') return isTuiResume(args);
  return args.length === 8 && args[0] === 'exec' && args[1] === '--sandbox' &&
    args[2] === 'workspace-write' && args[3] === '--json' &&
    args[4] === '--dangerously-bypass-hook-trust' && args[5] === 'resume' && args[6] === '--last';
}

function isTuiResume(args) {
  return args.length === TUI_FLAGS.length + 2 &&
    TUI_FLAGS.every((flag, index) => args[index] === flag) &&
    args[TUI_FLAGS.length] === 'resume' && args[TUI_FLAGS.length + 1] === '--last';
}

function isMissingResume(output, exitCode) {
  if (exitCode === 0 || typeof output !== 'string') return false;
  return /no saved (?:session|conversation|thread) found(?: to resume)?/i.test(output) ||
    /no session found with id\b/i.test(output);
}

module.exports = {
  name: 'Codex', stateDir: '.codex', authFilename: 'auth.json',
  // The TUI log only grows and the tmp dirs are per-process scratch; neither is needed to
  // resume, and every byte of them is re-compressed on each 30s persistence tick.
  // packages/ is the TUI's app-server daemon install (~400 MB of binaries the image already
  // ships) and app-server-daemon/ its pid and lock files: neither belongs to a conversation.
  stateExcludes: ['.codex/log', '.codex/tmp', '.codex/.tmp', '.codex/packages', '.codex/app-server-daemon'],
  attachmentCapabilities: Object.freeze({
    nativeImages: false, localImagePaths: true, mcpImages: true }),
  buildCommand, isResumeCommand, isMissingResume, prepare
};
