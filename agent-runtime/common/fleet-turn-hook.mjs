#!/usr/bin/env node
// Tells the session agent where the agent's turn stands, and turns waiting fleet messages into
// the agent's next turn when one ends (#173, docs/priority-messages.md).
//
//   Codex  requirements.toml  UserPromptSubmit, PermissionRequest  -> POST /agenthub/turn, no output
//                             Pre-/PostToolUse request_user_input  -> question / answered, no output
//                             Interrupt                            -> presentation idle, no output
//                             Stop                                 -> {"decision":"block","reason":<messages>}
//
// The session agent does the deciding: it takes the messages from the hub inbox, counts how
// often one turn has been continued this way, and knows whether the turn is draining for an
// update. This script only reports the event and prints what it is given back. Codex joins the
// reasons of every blocking Stop hook, so the skill reminder on the same Stop still gets through.
//
// Interactive sessions only. An autonomous run ends with its turn, and its plain messages stay
// in the inbox for agent_inbox, as they do for an autonomous Claude.

import { pathToFileURL } from 'node:url';
import agentQuestions from './agent-questions.js';

const CALL_TIMEOUT_MS = 8000;
const EVENTS = { UserPromptSubmit: 'prompt', PermissionRequest: 'permission', Stop: 'stop', Interrupt: 'idle', StopFailure: 'idle' };
// Tools whose PermissionRequest is a question to the person, not a request for permission (#252):
// Claude Code 2.1.287 raises PermissionRequest for AskUserQuestion to show its question screen.
// Reported as "permission", the Workspace said "Waiting for your approval" while it asked.
// Codex 0.160.0 raises no PermissionRequest for request_user_input, only PreToolUse before the
// question screen and PostToolUse once it is answered; both CLIs raise PostToolUse on the answer.
// The question goes with the report, from the tool's own input (#343): read off the screen, a
// question taller than the terminal lost its first lines.
const QUESTION_TOOLS = new Set(['AskUserQuestion', 'request_user_input']);
const TOOL_EVENTS = { PreToolUse: 'question', PostToolUse: 'answered' };

async function readStdin() {
  const chunks = [];
  let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    if (size > 1_000_000) break;
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

export function isInteractive(env = process.env) {
  return (env.AGENTHUB_MODE || 'interactive').toLowerCase() === 'interactive';
}

/**
 * The event to report, or null for one that is not the session's own thread. Codex runs helper
 * threads of its own — the task title is generated in a sub-session without a rollout file, and
 * subagents carry an agent_id — and their prompts and stops say nothing about whether the main
 * thread waits at its prompt. Reported, the title helper's prompt would mark an idle agent busy.
 */
export function eventOf(payload) {
  const event = EVENTS[payload.hook_event_name] ?? (QUESTION_TOOLS.has(payload.tool_name) ? TOOL_EVENTS[payload.hook_event_name] : undefined);
  if (!event) return null;
  if (typeof payload.turn_id === 'string' && payload.transcript_path == null) return null;
  if (payload.agent_id != null) return null;
  if (event === 'permission' && QUESTION_TOOLS.has(payload.tool_name)) return 'question';
  return event;
}

/** What goes with the event besides its name: the questions of a question tool's report. */
export function detailsOf(event, payload) {
  if (event !== 'question') return {};
  const questions = agentQuestions.normalizeQuestions(payload.tool_input);
  return questions.length ? { questions } : {};
}

export async function report(event, env = process.env, fetchImpl = fetch, details = {}) {
  const token = env.AGENTHUB_CALLBACK_TOKEN;
  if (!token) return null;
  const port = /^\d{1,5}$/.test(env.AGENTHUB_PORT || '') ? env.AGENTHUB_PORT : '7681';
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), CALL_TIMEOUT_MS);
  try {
    const response = await fetchImpl(`http://127.0.0.1:${port}/agenthub/turn`, {
      method: 'POST',
      headers: { 'X-Agent-Token': token, 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...details, event }),
      redirect: 'error',
      signal: controller.signal
    });
    if (!response.ok) return null;
    const body = await response.json();
    return typeof body?.text === 'string' && body.text.trim() ? body.text : null;
  } catch {
    return null;
  } finally {
    clearTimeout(timeout);
  }
}

/** What the hook prints: a continuation for a Stop that has messages to hand over, else nothing. */
export async function run(payload, env = process.env, fetchImpl = fetch) {
  if (!isInteractive(env)) return null;
  const event = eventOf(payload);
  if (!event) return null;
  const text = await report(event, env, fetchImpl, detailsOf(event, payload));
  if (event !== 'stop' || !text) return null;
  return { decision: 'block', reason: text };
}

/** Entry point, reached through codex/fleet-turn-hook.js. Prints nothing unless it blocks: Codex
 * rejects plain text on Stop, and empty stdout is "no decision" on every event used here. */
export async function cli() {
  const raw = await readStdin();
  let payload = {};
  try { payload = raw.trim() ? JSON.parse(raw) : {}; } catch { payload = {}; }
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) payload = {};
  const output = await run(payload);
  if (output) process.stdout.write(JSON.stringify(output));
}

// A hook that throws blocks the agent for no reason, so every failure path here is silent.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await cli().catch(() => {});
}
