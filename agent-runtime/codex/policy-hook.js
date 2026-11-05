#!/usr/bin/env node
'use strict';

const MAX_STDIN_BYTES = 256 * 1024;
const MAX_REQUEST_BYTES = 64 * 1024;
const MAX_RESPONSE_BYTES = 16 * 1024;
const CALLBACK_TIMEOUT_MS = 3000;
const DENY_REASON = 'Blocked by the session policy.';
const APPROVAL_TIMED_OUT = 'Nobody answered the AgentHub approval in time; tool call denied. Ask the agent to try again.';
const APPROVAL_UNAVAILABLE = 'AgentHub approval unavailable; tool call denied. Retry when approval is available.';

function emitDecision(eventName, behavior, reason = DENY_REASON) {
  if (eventName === 'PermissionRequest') {
    const decision = behavior === 'deny'
      ? { behavior: 'deny', message: reason }
      : { behavior: 'allow' };
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: { hookEventName: 'PermissionRequest', decision }
    }));
    return;
  }
  if (behavior === 'deny') {
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'deny',
        permissionDecisionReason: reason
      }
    }));
  }
}

function isMcpTool(tool) {
  return typeof tool === 'string' && tool.startsWith('mcp__');
}

function failClosed(eventName = 'PreToolUse') {
  emitDecision(eventName, 'deny', APPROVAL_UNAVAILABLE);
}

async function readInput() {
  let total = 0;
  let oversized = false;
  const chunks = [];
  for await (const chunk of process.stdin) {
    total += chunk.length;
    if (total > MAX_STDIN_BYTES) oversized = true;
    if (!oversized) chunks.push(chunk);
  }
  if (oversized) throw new Error('input limit');
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function callbackUrl(pathname) {
  const base = process.env.AGENTHUB_CALLBACK_URL;
  const token = process.env.AGENTHUB_CALLBACK_TOKEN;
  if (!base || !token) throw new Error('callback unavailable');
  const url = new URL(`${base.replace(/\/+$/, '')}${pathname}`);
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('callback unavailable');
  return { url, token };
}

async function readBoundedResponse(response) {
  if (!response.ok || !response.body) throw new Error('callback rejected');
  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_RESPONSE_BYTES) {
      await reader.cancel();
      throw new Error('response limit');
    }
    chunks.push(Buffer.from(value));
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

async function requestJson(pathname, options = {}) {
  // Policy reads and permission polls are safe to retry across a backend restart. A permission
  // POST may already have created a request, so retrying it could leave duplicate owner cards.
  const retryable = pathname === '/agent-policy' || (!options.method && pathname.startsWith('/permission/'));
  const attempts = retryable ? 3 : 1;
  for (let attempt = 0; attempt < attempts; attempt++) {
    try {
      return await requestJsonOnce(pathname, options);
    } catch (error) {
      if (attempt + 1 === attempts) throw error;
      await new Promise(resolve => setTimeout(resolve, 1000 * (attempt + 1)));
    }
  }
}

async function requestJsonOnce(pathname, options = {}) {
  const { url, token } = callbackUrl(pathname);
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), CALLBACK_TIMEOUT_MS);
  try {
    const init = {
      method: options.method || 'GET',
      headers: { 'X-Agent-Token': token },
      redirect: 'error',
      signal: controller.signal
    };
    if (options.body !== undefined) {
      const serialized = JSON.stringify(options.body);
      if (Buffer.byteLength(serialized) > MAX_REQUEST_BYTES) throw new Error('request limit');
      init.headers['Content-Type'] = 'application/json';
      init.body = serialized;
    }
    return await readBoundedResponse(await fetch(url, init));
  } finally {
    clearTimeout(timeout);
  }
}

function parseDecision(response, allowed) {
  if (!response || typeof response !== 'object' || Array.isArray(response)
      || !allowed.includes(response.decision)) throw new Error('invalid decision');
  return response.decision;
}

async function policyDecision(tool, input) {
  const response = await requestJson('/agent-policy', {
    method: 'POST', body: { tool, input }
  });
  return parseDecision(response, ['allow', 'deny', 'ask']);
}

function approvalDescriptor(tool) {
  if (tool === 'Bash') return 'Bash command';
  if (isMcpTool(tool)) return 'MCP tool request';
  if (tool === 'apply_patch' || tool === 'Edit' || tool === 'Write')
    return 'File change request';
  return 'Tool request';
}

function boundedInteger(value, fallback, minimum, maximum) {
  const parsed = Number.parseInt(value || '', 10);
  return Number.isInteger(parsed) ? Math.min(maximum, Math.max(minimum, parsed)) : fallback;
}

async function emitApproved(tool, input, eventName) {
  try {
    const recheck = await policyDecision(tool, input);
    emitDecision(eventName, recheck === 'deny' ? 'deny' : 'allow');
  } catch {
    failClosed(eventName);
  }
}

// Native approvals are disabled, so an unavailable or unanswered backend decision must deny
// in every mode rather than silently permit a tool or wait for a second approval surface.
async function handlePermissionRequest(tool, input, eventName = 'PermissionRequest') {
  const initial = await requestJson('/permission', {
    method: 'POST', body: { tool, input: approvalDescriptor(tool) }
  });
  if (typeof initial.decision === 'string') {
    const decision = parseDecision(initial, ['allow', 'allowAlways', 'deny', 'ask']);
    if (decision === 'allow' || decision === 'allowAlways') await emitApproved(tool, input, eventName);
    else if (decision === 'deny') emitDecision(eventName, 'deny');
    else failClosed(eventName);
    return;
  }
  if (typeof initial.id !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(initial.id)) {
    failClosed(eventName);
    return;
  }

  const interactive = (process.env.AGENTHUB_MODE || 'interactive').toLowerCase() === 'interactive';
  const seconds = interactive
    ? boundedInteger(process.env.AGENTHUB_CODEX_INTERACTIVE_APPROVAL_SECONDS, 1800, 1, 1800)
    : 240;
  const deadline = Date.now() + seconds * 1000;
  // Keep the legacy poll override for short proofs, but use elapsed time normally so slow
  // replies and retries do not turn the interactive window into an unbounded wait.
  const polls = boundedInteger(process.env.AGENTHUB_APPROVAL_POLLS, Number.MAX_SAFE_INTEGER, 1, interactive ? 900 : 120);
  const interval = boundedInteger(process.env.AGENTHUB_APPROVAL_INTERVAL_MS, 2000, 10, 2000);
  for (let attempt = 0; attempt < polls && Date.now() < deadline; attempt++) {
    await new Promise(resolve => setTimeout(resolve, Math.min(interval, deadline - Date.now())));
    const response = await requestJson(`/permission/${encodeURIComponent(initial.id)}`);
    const decision = parseDecision(response, ['allow', 'allowAlways', 'deny', 'pending', 'expired']);
    if (decision === 'expired') {
      emitDecision(eventName, 'deny', APPROVAL_TIMED_OUT);
      return;
    }
    if (decision === 'pending') continue;
    if (decision === 'deny') emitDecision(eventName, 'deny');
    else await emitApproved(tool, input, eventName);
    return;
  }
  // A healthy approval service returning pending is different from an unavailable service.
  emitDecision(eventName, 'deny', APPROVAL_TIMED_OUT);
}

async function main() {
  let payload;
  try {
    payload = await readInput();
  } catch {
    failClosed();
    return;
  }

  const eventName = payload?.hook_event_name;
  const tool = payload?.tool_name;
  const input = payload?.tool_input;
  const decisionEvent = eventName === 'PermissionRequest' ? eventName : 'PreToolUse';
  if (!['PreToolUse', 'PermissionRequest'].includes(eventName)
      || typeof tool !== 'string' || tool.length === 0 || tool.length > 256
      || input === null || typeof input !== 'object' || Array.isArray(input)) {
    failClosed(decisionEvent);
    return;
  }

  let policy;
  try {
    policy = await policyDecision(tool, input);
  } catch {
    failClosed(eventName);
    return;
  }

  if (policy === 'deny') {
    emitDecision(eventName, 'deny');
    return;
  }
  // Full access does not raise PermissionRequest. Resolve policy questions here in every
  // mode so the Workspace is the sole approval surface and no second CLI menu can hang.
  if (eventName === 'PreToolUse') {
    if (policy === 'ask') {
      try {
        await handlePermissionRequest(tool, input, eventName);
      } catch {
        failClosed(eventName);
      }
    }
    return;
  }

  try {
    await handlePermissionRequest(tool, input);
  } catch {
    failClosed(eventName);
  }
}

main().catch(() => failClosed());
