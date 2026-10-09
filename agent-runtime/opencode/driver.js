'use strict';

const fs = require('node:fs');
const path = require('node:path');

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
    if (env.AGENTHUB_OPENCODE_LOGIN === '1') {
      return { cmd: 'bash', args: [path.join(__dirname, 'login.sh'), ...args] };
    }
    return { cmd: 'opencode', args };
  }

  // No --auto here. Approval is the policy plugin's job in every mode; the managed config sets
  // OpenCode's own rules to allow so a call the plugin let through is not asked a second time,
  // which `run` would answer by rejecting it.
  const args = ['run'];
  if (restoredResume) args.push('--continue');
  args.push(prompt);
  return { cmd: 'opencode', args };
}

function isResumeCommand(command) {
  if (!command || !Array.isArray(command.args)) return false;
  if (command.cmd === 'bash' && command.args[0] !== path.join(__dirname, 'login.sh')) return false;
  if (command.cmd !== 'bash' && command.cmd !== 'opencode') return false;
  return command.args.includes('--continue');
}

function isMissingResume(output, exitCode) {
  if (exitCode === 0 || typeof output !== 'string') return false;
  return /session not found/i.test(output);
}

module.exports = {
  // ~/.local/share/opencode is linked to ~/.opencode by the entrypoint, so the session database,
  // undo snapshots and auth.json all live here. auth.json is excluded via authFilename; the MCP
  // OAuth store and logs are not state worth carrying either.
  name: 'OpenCode', stateDir: '.opencode', authFilename: 'auth.json',
  stateExcludes: ['.opencode/mcp-auth.json', '.opencode/log', '.opencode/bin'],
  attachmentCapabilities: Object.freeze({
    nativeImages: false, localImagePaths: true, mcpImages: true }),
  buildCommand, isResumeCommand, isMissingResume, prepare
};
