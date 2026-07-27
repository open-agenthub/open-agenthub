'use strict';

const fs = require('node:fs');
const path = require('node:path');

const API_KEY_ENVS = ['ANTHROPIC_API_KEY', 'OPENAI_API_KEY', 'CURSOR_API_KEY'];

function prepare(env) {
  if (env.AGENTHUB_STATE_RESTORED === undefined) {
    env.AGENTHUB_STATE_RESTORED = fs.existsSync('/tmp/.state-restored') ? '1' : '0';
  }

  // Pod mounts only the selected OpenClawApiKeySource key. Scrub all known
  // provider keys from the long-lived parent and pass present ones only to the child.
  const childEnv = {};
  for (const name of API_KEY_ENVS) {
    if (env[name]) {
      childEnv[name] = env[name];
      delete env[name];
    }
  }
  return Object.keys(childEnv).length > 0 ? { childEnv } : undefined;
}

function sessionId(env) {
  return env.AGENTHUB_OPENCLAW_SESSION_ID || env.AGENTHUB_CLAUDE_SESSION_ID || '';
}

function agentId(env) {
  return env.AGENTHUB_OPENCLAW_AGENT_ID || 'main';
}

function buildCommand(env, allowResume) {
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const prompt = env.AGENTHUB_PROMPT || '';
  const sid = sessionId(env);
  const restoredResume = allowResume && env.AGENTHUB_RESUME === '1' &&
    env.AGENTHUB_STATE_RESTORED === '1' && sid;

  if (mode === 'interactive') {
    const args = ['tui', '--local'];
    if (restoredResume) args.push('--session', sid);
    if (env.AGENTHUB_OPENCLAW_LOGIN === '1') {
      return { cmd: 'bash', args: [path.join(__dirname, 'login.sh'), ...args] };
    }
    return { cmd: 'openclaw', args };
  }

  const args = ['agent', '--local', '--agent', agentId(env)];
  if (restoredResume) args.push('--session-id', sid);
  args.push('--message', prompt);
  return { cmd: 'openclaw', args };
}

function isResumeCommand(command) {
  if (!command || !Array.isArray(command.args)) return false;
  const args = command.args;
  if (command.cmd === 'bash') {
    const loginSh = path.join(__dirname, 'login.sh');
    if (args[0] !== loginSh) return false;
    const sessionIndex = args.indexOf('--session');
    return sessionIndex >= 0 && typeof args[sessionIndex + 1] === 'string' &&
      args[sessionIndex + 1].length > 0;
  }
  if (command.cmd !== 'openclaw') return false;
  const sessionIndex = args.indexOf('--session');
  if (sessionIndex >= 0 && typeof args[sessionIndex + 1] === 'string' &&
    args[sessionIndex + 1].length > 0) {
    return true;
  }
  const sessionIdIndex = args.indexOf('--session-id');
  return sessionIdIndex >= 0 && typeof args[sessionIdIndex + 1] === 'string' &&
    args[sessionIdIndex + 1].length > 0;
}

function isMissingResume(output, exitCode) {
  if (exitCode === 0 || typeof output !== 'string') return false;
  return /no session found(?: with id)?/i.test(output) ||
    /session not found/i.test(output) ||
    /unknown session(?: id)?/i.test(output);
}

module.exports = {
  // authFilename: AgentHub Secret key + root state-tar exclusion under ~/.openclaw/.
  // stateExcludes: nested agent-store credentials (JSON + SQLite) for every agent id.
  // Runtime restore imports Secret into agents/main/agent/ via sync-auth-profiles.js.
  name: 'OpenClaw', stateDir: '.openclaw', authFilename: 'auth-profiles.json',
  stateExcludes: [
    '.openclaw/agents/main/agent/auth-profiles.json',
    '.openclaw/agents/main/agent/openclaw-agent.sqlite',
    '.openclaw/agents/main/agent/openclaw-agent.sqlite-wal',
    '.openclaw/agents/main/agent/openclaw-agent.sqlite-shm',
    '.openclaw/agents/*/agent/auth-profiles.json',
    '.openclaw/agents/*/agent/openclaw-agent.sqlite',
    '.openclaw/agents/*/agent/openclaw-agent.sqlite-wal',
    '.openclaw/agents/*/agent/openclaw-agent.sqlite-shm'
  ],
  buildCommand, isResumeCommand, isMissingResume, prepare
};
