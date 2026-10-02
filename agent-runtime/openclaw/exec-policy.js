'use strict';

const fs = require('node:fs');
const path = require('node:path');

/**
 * Translates the session's auto-approve flag and command policy into OpenClaw's exec policy.
 *
 * OpenClaw was the one runtime with no permission wiring at all: no per-call hook, no allow list,
 * nothing calling the hub's approval endpoint. An unattended session therefore ran under whatever
 * the restored state directory happened to contain, and switching auto-approve off changed
 * nothing — the flag had no way of reaching the agent.
 *
 * Unlike the other three this cannot be a hook. OpenClaw's `hooks` are plugin packs for lifecycle
 * events (`openclaw hooks list`: boot-md, command-logger, session-memory ...), not a command asked
 * about each tool call, so there is no place to put a callback to `/permission`. What it does have
 * is an exec policy, and that is what the flag maps onto.
 *
 * Verified against the pinned CLI (2026.7.1-2):
 *   - `openclaw exec-policy set --security <deny|allowlist|full> --ask <off|on-miss|always>
 *      --ask-fallback <deny|allowlist|full>` writes `tools.exec` in `openclaw.json` and
 *      `defaults` in `exec-approvals.json`, and needs no gateway.
 *   - `openclaw approvals allowlist add --agent <id> <pattern>` appends
 *     `agents.<id>.allowlist[].pattern` to the same approvals file.
 *   - `openclaw exec-policy show` reports the effective policy as "the host approvals file
 *     intersected with requested tools.exec policy", which is why both files are written.
 *
 * The CLI is not shelled out to: it rewrites whole files and `openclaw.json` is shared with
 * mcp-config.js, which runs first.
 */

// One consequence of the intersection above: a value only takes effect if it is in both files.
const SECURITY = new Set(['deny', 'allowlist', 'full']);
const ASK = new Set(['off', 'on-miss', 'always']);

/**
 * The session's shell policy as OpenClaw allow-list patterns.
 *
 * The two policies are not the same shape. AgentHub stores command prefixes ("git status"),
 * OpenClaw matches a glob against the resolved binary, so only the first token survives the
 * translation — "git status" becomes a licence to run git, and the argument part is dropped. That
 * is a widening, so it is only ever reached with `security: allowlist`, where the alternative is
 * the agent being unable to run anything at all.
 */
function allowlistPatterns(allowedCommands) {
  const patterns = [];
  for (const command of allowedCommands) {
    const binary = command.trim().split(/\s+/)[0];
    // A path keeps its anchor; a bare name is matched wherever it resolves from PATH.
    if (!binary || /[*?[\]{}]/.test(binary)) continue;
    patterns.push(binary.includes('/') ? binary : `**/${binary}`);
  }
  return [...new Set(patterns)];
}

function parseCommands(raw) {
  if (!raw) return [];
  let values;
  try {
    values = JSON.parse(raw);
  } catch {
    throw new Error('Invalid OpenClaw shell policy JSON.');
  }
  if (!Array.isArray(values) || values.some(value => typeof value !== 'string')) {
    throw new Error('Invalid OpenClaw shell policy JSON.');
  }
  return values.map(value => value.trim()).filter(Boolean);
}

/**
 * Decides the policy for a session.
 *
 * Interactive sessions ask OpenClaw's own UI (`ask: on-miss`) because the person is at the
 * terminal — the hub's chat relay cannot be reached from here, which is a documented limitation
 * rather than something to fake. Unattended sessions never ask, because nobody would hear it:
 * auto-approve decides between running everything and running only the allow list.
 */
function resolvePolicy(env = process.env) {
  const interactive = (env.AGENTHUB_MODE || 'interactive').toLowerCase() === 'interactive';
  // Absent means off. A pod started before this variable existed must not read as auto-approving.
  const autoApprove = env.AGENTHUB_AUTO_APPROVE === '1';

  if (interactive) return { security: 'allowlist', ask: 'on-miss', askFallback: 'deny' };
  if (autoApprove) return { security: 'full', ask: 'off', askFallback: 'deny' };
  return { security: 'allowlist', ask: 'off', askFallback: 'deny' };
}

/** Replaces `tools.exec` and leaves every other key — including mcp.servers — untouched. */
function mergeConfig(config, policy) {
  const base = config && typeof config === 'object' && !Array.isArray(config) ? config : {};
  const tools = base.tools && typeof base.tools === 'object' && !Array.isArray(base.tools)
    ? base.tools : {};
  return {
    ...base,
    tools: { ...tools, exec: { security: policy.security, ask: policy.ask } }
  };
}

/**
 * The approvals file, written whole rather than merged.
 *
 * `~/.openclaw` is the state directory and comes back from the session's own archive, so a merge
 * would restore whatever allow-list entries an earlier incarnation had accumulated — including any
 * the agent added itself with `openclaw approvals allowlist add`. A permission the hub never
 * granted must not survive a restart, so this file is derived from the session every start.
 */
function approvalsFile(policy, patterns, agentId) {
  const file = {
    version: 1,
    socket: {},
    defaults: { security: policy.security, ask: policy.ask, askFallback: policy.askFallback },
    agents: {}
  };
  if (policy.security === 'allowlist' && patterns.length) {
    file.agents[agentId] = { allowlist: patterns.map(pattern => ({ pattern })) };
  }
  return file;
}

function writeExecPolicy(configPath, approvalsPath, env = process.env) {
  const policy = resolvePolicy(env);
  if (!SECURITY.has(policy.security) || !ASK.has(policy.ask)) {
    throw new Error('Invalid OpenClaw exec policy.');
  }
  const agentId = env.AGENTHUB_OPENCLAW_AGENT_ID || 'main';
  const patterns = allowlistPatterns(parseCommands(env.AGENTHUB_ALLOWED_COMMANDS));

  let existing = {};
  if (configPath && fs.existsSync(configPath)) {
    try {
      const parsed = JSON.parse(fs.readFileSync(configPath, 'utf8'));
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) existing = parsed;
    } catch { existing = {}; }
  }

  fs.mkdirSync(path.dirname(configPath), { recursive: true });
  fs.writeFileSync(configPath,
    `${JSON.stringify(mergeConfig(existing, policy), null, 2)}\n`, { mode: 0o600 });
  fs.mkdirSync(path.dirname(approvalsPath), { recursive: true });
  fs.writeFileSync(approvalsPath,
    `${JSON.stringify(approvalsFile(policy, patterns, agentId), null, 2)}\n`, { mode: 0o600 });
  return { policy, patterns, agentId };
}

if (require.main === module) {
  try {
    const result = writeExecPolicy(process.argv[2], process.argv[3]);
    process.stdout.write(
      `security=${result.policy.security} ask=${result.policy.ask} ` +
      `allowlist=${result.patterns.length}\n`);
  } catch (error) {
    console.error('[openclaw-exec-policy] Policy rejected: ' + error.message);
    process.exit(1);
  }
}

module.exports = {
  allowlistPatterns, parseCommands, resolvePolicy, mergeConfig, approvalsFile, writeExecPolicy
};
