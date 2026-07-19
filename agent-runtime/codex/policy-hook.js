#!/usr/bin/env node
'use strict';

const MAX_STDIN_BYTES = 256 * 1024;
const MAX_REQUEST_BYTES = 64 * 1024;
const MAX_RESPONSE_BYTES = 16 * 1024;
const CALLBACK_TIMEOUT_MS = 3000;
const DENY_REASON = 'Blocked by the session policy.';

function isInteractive() {
  return (process.env.AGENTHUB_MODE || 'interactive').toLowerCase() === 'interactive';
}

function emitDecision(eventName, behavior) {
  if (eventName === 'PermissionRequest') {
    const decision = behavior === 'deny'
      ? { behavior: 'deny', message: DENY_REASON }
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
        permissionDecisionReason: DENY_REASON
      }
    }));
  }
}

function failClosed(eventName = 'PreToolUse') {
  if (!isInteractive()) emitDecision(eventName, 'deny');
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

function approvalSummary(input) {
  try { return JSON.stringify(input).slice(0, 800); } catch { return ''; }
}

function boundedInteger(value, fallback, minimum, maximum) {
  const parsed = Number.parseInt(value || '', 10);
  return Number.isInteger(parsed) ? Math.min(maximum, Math.max(minimum, parsed)) : fallback;
}

async function handlePermissionRequest(tool, input) {
  const initial = await requestJson('/permission', {
    method: 'POST', body: { tool, input: approvalSummary(input) }
  });
  if (typeof initial.decision === 'string') {
    const decision = parseDecision(initial, ['allow', 'allowAlways', 'deny', 'ask']);
    if (decision === 'allow' || decision === 'allowAlways') emitDecision('PermissionRequest', 'allow');
    else if (decision === 'deny') emitDecision('PermissionRequest', 'deny');
    return;
  }
  if (typeof initial.id !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(initial.id)) return;

  const polls = boundedInteger(process.env.AGENTHUB_APPROVAL_POLLS, 120, 1, 120);
  const interval = boundedInteger(process.env.AGENTHUB_APPROVAL_INTERVAL_MS, 2000, 10, 2000);
  for (let attempt = 0; attempt < polls; attempt++) {
    await new Promise(resolve => setTimeout(resolve, interval));
    const response = await requestJson(`/permission/${encodeURIComponent(initial.id)}`);
    const decision = parseDecision(response, ['allow', 'allowAlways', 'deny', 'pending']);
    if (decision === 'pending') continue;
    emitDecision('PermissionRequest', decision === 'deny' ? 'deny' : 'allow');
    return;
  }
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
  if (!['PreToolUse', 'PermissionRequest'].includes(eventName)
      || typeof tool !== 'string' || tool.length === 0 || tool.length > 256
      || input === undefined) {
    failClosed(eventName === 'PermissionRequest' ? eventName : 'PreToolUse');
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
  if (eventName === 'PreToolUse') {
    if (policy === 'ask' && !isInteractive()) emitDecision(eventName, 'deny');
    return;
  }
  if (!isInteractive()) {
    emitDecision(eventName, policy === 'ask' ? 'deny' : 'allow');
    return;
  }

  try {
    await handlePermissionRequest(tool, input);
  } catch {
    // Interactive callback failures deliberately leave the normal Codex prompt available.
  }
}

main().catch(() => failClosed());
