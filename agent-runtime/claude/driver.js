'use strict';

const fs = require('node:fs');
const path = require('node:path');

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
  const uiMode = (env.AGENTHUB_UI_MODE || 'terminal').toLowerCase();
  const prompt = env.AGENTHUB_PROMPT || '';
  const systemPrompt = env.AGENTHUB_SYSTEM_PROMPT || '';
  const sessionId = env.AGENTHUB_CLAUDE_SESSION_ID || '';
  const args = [];

  if (env.AGENTHUB_MCP_CONFIG) args.push('--mcp-config', env.AGENTHUB_MCP_CONFIG);
  else if (env.AGENTHUB_HAS_MCP === '1') args.push('--mcp-config', '/secrets/mcp/mcp.json');

  const resuming = allowResume && env.AGENTHUB_RESUME === '1' && sessionId &&
    env.AGENTHUB_STATE_RESTORED === '1';
  if (resuming) {
    args.push('--resume', sessionId);
  } else if (sessionId) {
    args.push('--session-id', sessionId);
  }

  // --append-system-prompt, not --system-prompt: the latter *replaces* Claude Code's own system
  // prompt, taking its tool and environment instructions with it, so a caller adding one line of
  // persona would silently disable the agent.
  if (systemPrompt) args.push('--append-system-prompt', systemPrompt);

  if (mode === 'interactive' && uiMode === 'chat') {
    // Print mode with streaming JSON on both ends keeps the process alive for
    // multiple turns; --verbose is required for stream-json output with -p.
    args.push('-p', '--input-format', 'stream-json', '--output-format', 'stream-json',
      '--include-partial-messages', '--verbose');
    return { cmd: 'claude', args, pipe: true };
  }

  if (mode !== 'interactive') {
    args.push('-p', prompt, '--permission-mode', 'acceptEdits');
    const allowed = nativeAllowedTools(env);
    if (allowed.length) args.push('--allowedTools', allowed.join(','));
    return { cmd: 'claude', args };
  }

  // A trailing positional argument is the initial prompt of an *interactive* session: the CLI
  // submits it at startup and then keeps the live REPL, so the session is already working when a
  // person opens its terminal. Verified against 2.1.283 — the request goes out with no keystroke
  // and the input box stays there afterwards. Using -p instead would answer once and exit, and
  // there would be nothing left to take over.
  //
  // Never on a resume: the conversation already holds the task, and re-submitting it would make
  // a resumed session start its work from the top.
  if (prompt && !resuming) args.push(prompt);

  return { cmd: 'claude', args };
}

function isResumeCommand(command) {
  return command.args.includes('--resume');
}

// Only the CLI's own words ("No conversation found with session ID: …", "No conversation found
// to continue") mean the saved conversation is gone. This used to treat any non-zero exit within
// ten seconds the same way, so an expired login or an unreachable API — the two fastest ways
// for a resume to die — restarted the session fresh and dropped --resume along with the whole
// history, when a plain retry after the person fixed the cause would have kept it.
function isMissingResume(output, exitCode) {
  return exitCode !== 0 && output.includes('No conversation found');
}

// Claude Code files a conversation under the directory it was started in, with every character
// outside [A-Za-z0-9] replaced by a dash, and names it after the session id. Both halves are
// fixed by us — the id through --session-id/--resume, the directory through the PTY's cwd — so
// the path is known in advance rather than discovered; it just does not exist until the first
// turn has been written. Nothing is persisted from the archive on a resume here either: the
// same file is appended to, because --resume keeps the id.
function findTranscript({ env, home, cwd, fs: fileSystem }) {
  const sessionId = env.AGENTHUB_CLAUDE_SESSION_ID || '';
  if (!sessionId) return null;
  const slug = cwd.replace(/[^A-Za-z0-9]/g, '-');
  const file = path.join(home, '.claude', 'projects', slug, sessionId + '.jsonl');
  return fileSystem.existsSync(file) ? file : null;
}

module.exports = {
  name: 'Claude',
  stateDir: '.claude',
  authFilename: '.credentials.json',
  attachmentCapabilities: Object.freeze({
    nativeImages: false, localImagePaths: true, mcpImages: true }),
  buildCommand,
  isResumeCommand,
  isMissingResume,
  findTranscript,
  prepare
};
