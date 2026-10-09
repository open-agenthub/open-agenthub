import fs from 'node:fs';

/**
 * The decision logic behind policy-plugin.mjs, kept apart so it can be tested on its own.
 *
 * AgentHub's approval gate for OpenCode, registered from the managed config (see
 * managed-config.js) so the agent cannot remove it.
 *
 * `tool.execute.before` and not `permission.ask`: the latter is declared in the plugin types of
 * 1.18.x but nothing calls it, so a gate built on it never runs. `tool.execute.before` fires for
 * every tool call before OpenCode evaluates its own permission rules, and a throw there fails the
 * call with the thrown message — which the model sees and works around, like a denied hook in the
 * other runtimes. Verified against the pinned CLI with a mock model: a throw blocked a bash call and
 * the turn went on to its final answer.
 *
 * The decision itself follows Codex's policy hook: ask /agent-policy, and when that says "ask",
 * create a /permission request and poll it. The session's auto-approve flag is read by the backend
 * on every call, so it can be toggled while the session runs.
 */

const DENY_REASON = 'Blocked by the session policy.';
const CALLBACK_TIMEOUT_MS = 5000;
const MAX_RESPONSE_BYTES = 16 * 1024;

// Conversation bookkeeping with no effect outside the session. Claude and Codex have no
// equivalent that reaches their hooks as a separate call, and putting an approval question in
// front of "update the todo list" would only teach people to click allow without reading.
const UNGATED = new Set(['todowrite', 'todoread', 'question', 'invalid']);

// OpenCode's built-in tools under the names the hub's policy already uses for Claude and Codex,
// so one allow list ("Read", "Edit", commands for Bash) means the same thing for every agent.
const TOOL_NAMES = Object.freeze({
  bash: 'Bash',
  read: 'Read',
  edit: 'Edit',
  multiedit: 'Edit',
  patch: 'Edit',
  apply_patch: 'Edit',
  write: 'Write',
  glob: 'Glob',
  grep: 'Grep',
  list: 'LS',
  webfetch: 'WebFetch',
  websearch: 'WebSearch',
  codesearch: 'CodeSearch',
  task: 'Task',
  skill: 'Skill',
  lsp: 'LSP',
  batch: 'Batch'
});

function isInteractive(env) {
  return (env.AGENTHUB_MODE || 'interactive').toLowerCase() === 'interactive';
}

/** Server names of the session's MCP config, longest first so a prefix cannot shadow a longer name. */
export function mcpServerNames(env = process.env, fsImpl = fs) {
  try {
    const parsed = JSON.parse(fsImpl.readFileSync(env.AGENTHUB_MCP_CONFIG, 'utf8'));
    const servers = parsed && typeof parsed.mcpServers === 'object' && !Array.isArray(parsed.mcpServers)
      ? Object.keys(parsed.mcpServers) : [];
    return servers.filter(name => /^[A-Za-z0-9_-]+$/.test(name)).sort((a, b) => b.length - a.length);
  } catch {
    return [];
  }
}

/**
 * The hub's name and policy input for one OpenCode tool call. MCP tools reach OpenCode as
 * `<server>_<tool>`; they become `mcp__<server>__<tool>` so the MCP sharing policy and the MCP
 * allow list match them exactly as they do for Claude.
 */
export function hubTool(tool, args, servers = []) {
  if (tool === 'bash') {
    const command = args && typeof args.command === 'string' ? args.command : '';
    return { name: 'Bash', input: { command } };
  }
  if (Object.hasOwn(TOOL_NAMES, tool)) return { name: TOOL_NAMES[tool], input: {} };
  for (const server of servers) {
    const prefix = server + '_';
    if (tool.startsWith(prefix) && tool.length > prefix.length) {
      return { name: 'mcp__' + server + '__' + tool.slice(prefix.length), input: {} };
    }
  }
  return { name: tool, input: {} };
}

function approvalDescriptor(name) {
  if (name === 'Bash') return 'Bash command';
  if (name.startsWith('mcp__')) return 'MCP tool request';
  if (name === 'Edit' || name === 'Write') return 'File change request';
  return 'Tool request';
}

function boundedInteger(value, fallback, minimum, maximum) {
  const parsed = Number.parseInt(value || '', 10);
  return Number.isInteger(parsed) ? Math.min(maximum, Math.max(minimum, parsed)) : fallback;
}

export function createGate({ env = process.env, fetchImpl = globalThis.fetch,
  sleep = ms => new Promise(resolve => setTimeout(resolve, ms)), servers } = {}) {
  const serverNames = servers || mcpServerNames(env);

  async function request(pathname, options = {}) {
    const base = env.AGENTHUB_CALLBACK_URL;
    const token = env.AGENTHUB_CALLBACK_TOKEN;
    if (!base || !token) throw new Error('callback unavailable');
    const url = new URL(base.replace(/\/+$/, '') + pathname);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('callback unavailable');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), CALLBACK_TIMEOUT_MS);
    try {
      const init = { method: options.method || 'GET', headers: { 'X-Agent-Token': token },
        redirect: 'error', signal: controller.signal };
      if (options.body !== undefined) {
        init.headers['Content-Type'] = 'application/json';
        init.body = JSON.stringify(options.body);
      }
      const response = await fetchImpl(url, init);
      if (!response || !response.ok) throw new Error('callback rejected');
      const text = await response.text();
      if (text.length > MAX_RESPONSE_BYTES) throw new Error('response limit');
      return JSON.parse(text);
    } finally {
      clearTimeout(timer);
    }
  }

  function decision(response, allowed) {
    if (!response || typeof response !== 'object' || !allowed.includes(response.decision)) {
      throw new Error('invalid decision');
    }
    return response.decision;
  }

  async function policy(name, input) {
    return decision(await request('/agent-policy', { method: 'POST', body: { tool: name, input } }),
      ['allow', 'deny', 'ask']);
  }

  // An approval only stands if the policy still does not deny the call: the MCP sharing policy can
  // change while someone is deciding, and it is a hard deny either way.
  async function approved(name, input) {
    return (await policy(name, input)) !== 'deny';
  }

  async function askPermission(name, input) {
    const initial = await request('/permission', {
      method: 'POST', body: { tool: name, input: approvalDescriptor(name) }
    });
    if (typeof initial.decision === 'string') {
      const answer = decision(initial, ['allow', 'allowAlways', 'deny', 'ask']);
      return (answer === 'allow' || answer === 'allowAlways') && await approved(name, input);
    }
    if (typeof initial.id !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(initial.id)) return false;

    // Someone at the session can take their time; an unattended run that nobody answers should
    // not hold its pod for half an hour. Same windows as Claude's hook and Codex's, respectively.
    const interval = boundedInteger(env.AGENTHUB_APPROVAL_INTERVAL_MS, 2000, 10, 5000);
    const windowMs = isInteractive(env)
      ? boundedInteger(env.AGENTHUB_PERMISSION_POLL_SECONDS, 1740, 1, 1740) * 1000
      : boundedInteger(env.AGENTHUB_APPROVAL_POLLS, 120, 1, 120) * interval;
    const id = encodeURIComponent(initial.id);
    for (let waited = 0; waited < windowMs; waited += interval) {
      await sleep(interval);
      let answer;
      try {
        answer = decision(await request('/permission/' + id), ['allow', 'allowAlways', 'deny', 'pending', 'expired']);
      } catch {
        continue;
      }
      if (answer === 'pending') continue;
      return (answer === 'allow' || answer === 'allowAlways') && await approved(name, input);
    }
    // Gave up: expire the request so the chat prompt is defused. A click that won the race
    // against the expiry is still honoured.
    try {
      const last = await request('/permission/' + id + '/expire', { method: 'POST' });
      return (last.decision === 'allow' || last.decision === 'allowAlways') && await approved(name, input);
    } catch {
      return false;
    }
  }

  /** Resolves when the call may run; throws with the reason the model will see otherwise. */
  return async function gate(tool, args) {
    if (typeof tool !== 'string' || !tool || tool.length > 256) throw new Error(DENY_REASON);
    if (UNGATED.has(tool)) return;
    const { name, input } = hubTool(tool, args, serverNames);
    // Every path without an answer ends in a denial. OpenCode's own rules are set to allow, so
    // there is no prompt behind this one to fall back on — not even in an interactive session.
    let verdict;
    try {
      verdict = await policy(name, input);
    } catch {
      throw new Error(DENY_REASON + ' The approval service could not be reached.');
    }
    if (verdict === 'allow') return;
    if (verdict === 'deny') throw new Error(DENY_REASON);
    let ok = false;
    try {
      ok = await askPermission(name, input);
    } catch {
      ok = false;
    }
    if (!ok) throw new Error(DENY_REASON + ' The request was not approved.');
  };
}
