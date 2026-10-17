'use strict';

const fs = require('node:fs');
const path = require('node:path');

const { writeCredentialFile } = require('../common/credential-install');
const { validCredential } = require('./auth-watcher');

const LOGIN_SCRIPT = path.join(__dirname, 'login.sh');
const REFRESH_SCRIPT = path.join(__dirname, 'refresh-config.sh');
// Set by installCredential, consumed by the next buildCommand. Kept on the server's env object
// rather than the child's, which is fixed when the server starts.
const REFRESH_FLAG = 'AGENTHUB_OPENCODE_REFRESH_CONFIG';

function prepare(env) {
  if (env.AGENTHUB_STATE_RESTORED === undefined) {
    env.AGENTHUB_STATE_RESTORED = fs.existsSync('/tmp/.state-restored') ? '1' : '0';
  }

  // OPENCODE_API_KEY is how OpenCode Go (and Zen) authenticate without a stored login. Scrub it
  // from the long-lived server/shell env and pass it only to the agent child.
  const apiKey = env.OPENCODE_API_KEY;
  delete env.OPENCODE_API_KEY;
  if (apiKey) return { childEnv: { OPENCODE_API_KEY: apiKey } };
  return undefined;
}

function buildCommand(env, allowResume) {
  // The default model in the user config was chosen for the login the session started with. A
  // login swapped in by the hub may be for another provider, and a Go model left in the config
  // would then fail every request, so the restart regenerates the config first. In the PTY, not
  // in installCredential: listing the Go catalogue runs the CLI for seconds, and the swap request
  // must answer before the hub's timeout.
  const refresh = env[REFRESH_FLAG] === '1';
  delete env[REFRESH_FLAG];
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const prompt = env.AGENTHUB_PROMPT || '';
  // --continue rather than a session id: OpenCode generates its own ids (ses_…) and has no way
  // to be handed one up front, while --continue picks the newest top-level session of the
  // project. Measured on 1.18.34: with no session to continue it starts a fresh one and exits 0,
  // so there is no missing-resume failure to fall back from.
  const restoredResume = allowResume && env.AGENTHUB_RESUME === '1' &&
    env.AGENTHUB_STATE_RESTORED === '1';

  if (mode === 'interactive') {
    const args = [];
    if (restoredResume) args.push('--continue');
    // The TUI submits --prompt as the first message, so a session created by an API arrives with
    // its task under way. Not on a resume, where the conversation already holds it.
    if (prompt && !restoredResume) args.push('--prompt', prompt);
    if (refresh) return { cmd: 'bash', args: [REFRESH_SCRIPT, ...args] };
    if (env.AGENTHUB_OPENCODE_LOGIN === '1') {
      return { cmd: 'bash', args: [LOGIN_SCRIPT, ...args] };
    }
    return { cmd: 'opencode', args };
  }

  // No --auto here. Approval is the policy plugin's job in every mode; the managed config sets
  // OpenCode's own rules to allow so a call the plugin let through is not asked a second time,
  // which `run` would answer by rejecting it.
  const args = ['run'];
  if (restoredResume) args.push('--continue');
  args.push(prompt);
  if (refresh) return { cmd: 'bash', args: [REFRESH_SCRIPT, ...args] };
  return { cmd: 'opencode', args };
}

function isResumeCommand(command) {
  if (!command || !Array.isArray(command.args)) return false;
  if (command.cmd === 'bash' && command.args[0] !== LOGIN_SCRIPT && command.args[0] !== REFRESH_SCRIPT) return false;
  if (command.cmd !== 'bash' && command.cmd !== 'opencode') return false;
  return command.args.includes('--continue');
}

// The canonical auth.json the entrypoint links OpenCode's data directory to, which is also
// stateDir/authFilename; the override only matters if the image moved it.
function credentialPath(env) {
  return env.OPENCODE_AUTH_FILE || null;
}

function installCredential(env, body, target, fsImpl = fs) {
  writeCredentialFile(target, body, fsImpl);
  env[REFRESH_FLAG] = '1';
}

function isMissingResume(output, exitCode) {
  if (exitCode === 0 || typeof output !== 'string') return false;
  return /session not found/i.test(output);
}

// OpenCode's own limit notices, read from the binary of opencode-ai 1.18.35 (the pinned version).
// It composes two of them from an OpenCode Go/Zen error body and shows them as the session's
// retry status, then waits and retries instead of failing:
// "<limit name> usage limit reached. It will reset in 2 hours 15 minutes. To continue using this
// model now, enable usage from your available balance" (a Go window, retried after the
// response's retry-after) and "Free usage exceeded, subscribe to Go" (the free tier). The third
// is its text for an OpenAI `insufficient_quota` error. Every other limit message it shows is the
// provider's own text passed through, which is no OpenCode sentence to pin to.
// The TUI shows a retry status cut to 80 characters, which keeps the reset duration for any
// limit name of ordinary length; `opencode run` shows none, so limit-notice.mjs prints it there.
const limitPatterns = [
  { pattern: /usage limit reached\. It will reset in/i, resetsAt: resetFromDuration },
  { pattern: /Free usage exceeded, subscribe to Go/ },
  { pattern: /Quota exceeded\. Check your plan and billing details\./ }
];

// "It will reset in 1 day 3 hours" / "2 hours 15 minutes" / "less than a minute", the shapes
// OpenCode formats retry-after into. Relative, so unlike a wall-clock time it does not depend on a
// time zone the pod does not share with the person. An empty duration (no retry-after) is null.
function resetFromDuration(match, text, { secondsFromNow }) {
  const rest = text.slice(match.index + match[0].length, match.index + match[0].length + 60);
  if (/^\s*less than a minute/.test(rest)) return secondsFromNow(60);
  const parts = /^\s*(?:(\d+) days?)?\s*(?:(\d+) hours?)?\s*(?:(\d+) minutes?)?/.exec(rest);
  const seconds = (Number(parts[1] || 0) * 86400) + (Number(parts[2] || 0) * 3600) + (Number(parts[3] || 0) * 60);
  return seconds > 0 ? secondsFromNow(seconds) : null;
}

module.exports = {
  limitPatterns,
  // ~/.local/share/opencode is linked to ~/.opencode by the entrypoint, so the session database,
  // undo snapshots and auth.json all live here. auth.json is excluded via authFilename; the MCP
  // OAuth store and logs are not state worth carrying either.
  name: 'OpenCode', stateDir: '.opencode', authFilename: 'auth.json',
  stateExcludes: ['.opencode/mcp-auth.json', '.opencode/log', '.opencode/bin'],
  attachmentCapabilities: Object.freeze({
    nativeImages: false, localImagePaths: true, mcpImages: true }),
  buildCommand, isResumeCommand, isMissingResume, prepare,
  credentialPath, validCredential, installCredential
};
