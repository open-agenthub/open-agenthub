#!/usr/bin/env node
// End-of-turn "the agent is waiting for you" relay for Codex and Cursor.
//
// The chat relays (Slack, Telegram, Signal) open a session's thread only on a "question"
// event, and that event is produced by exactly one thing: an agent hook posting to
// /internal/sessions/{id}/notify. Claude has its Notification hook for it. Codex and Cursor
// had nothing, so their sessions never appeared in chat at all — not even "finished", which
// the relays drop when no thread exists. This hook is the missing half: at the end of a turn
// it sends the agent's last message as the question.
//
// One script for both agents, reached the same way skill-reminder-hook.mjs is:
//   Codex    requirements.toml [[hooks.Stop]]  (via codex/turn-notify-hook.js, managed_dir)
//   Cursor   hooks.json hooks.stop
//
// Interactive sessions only. In exec/autonomous mode the end of the turn is the end of the
// process: the session-agent then posts Succeeded, which the relays carry as "finished".
// A "question" there would open a thread for an answer nobody can give.
//
// Codex names the last assistant message outright; the transcript is the fallback. Cursor's
// stop payload carries neither, so its question is a generic line — the thread still opens
// and the web terminal (linked in the header) has the text.
//
// Timing caveat, documented rather than solved: the skill reminder runs on the same Stop and
// may continue the turn once. That continuation ends with stop_hook_active (Codex) or
// loop_count > 0 (Cursor), which this hook ignores — so the chat sees the substantive
// answer a few seconds before the agent is truly idle, instead of the reminder's one-line
// reply. A reply from chat takes longer than that turn, so the window is theoretical.

import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

const DEFAULT_MESSAGE = 'The agent finished its turn and is waiting for your reply.';
// Same cap as the Claude notification hook; the relays split the text further.
const MAX_MESSAGE_CHARS = 12_000;
const MAX_TRANSCRIPT_BYTES = 8 * 1024 * 1024;
const CALLBACK_TIMEOUT_MS = 3000;

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

/** Unset means interactive everywhere else in the runtime (driver, policy hook); same here. */
export function isInteractive(env = process.env) {
  return (env.AGENTHUB_MODE || 'interactive').toLowerCase() === 'interactive';
}

/** True when this stop is the continuation a Stop hook (the skill reminder) caused. */
function isHookContinuation(payload) {
  return payload.stop_hook_active === true || Number(payload.loop_count) > 0;
}

/**
 * A Codex thread without a rollout file is one of the TUI's helpers, not the user's thread.
 * Seen on 0.160.0: after the first answer the TUI generates a task title in a sub-session of
 * its own (new session_id, transcript_path null, bypassPermissions), and that sub-session's
 * Stop fires this hook as well — with the title as its last message. Relayed, that would be
 * a second "question" in chat reading "Answer the branch question". Cursor has no turn_id,
 * so its (normally null) transcript_path is not read this way.
 */
function isCodexHelperThread(payload) {
  return typeof payload.turn_id === 'string' && payload.transcript_path == null;
}

/** Text of one transcript entry's assistant message, whatever shape the agent writes. */
function assistantText(entry) {
  // Codex rollout: {type:"response_item", payload:{type:"message", role:"assistant", content:[{type:"output_text", text}]}}
  // Claude-style:  {type:"assistant", message:{content:[{type:"text", text}]}}
  const message = entry.payload ?? entry.message ?? entry;
  const role = message.role ?? (entry.type === 'assistant' ? 'assistant' : undefined);
  if (role !== 'assistant' || !Array.isArray(message.content)) return null;
  const text = message.content
    .filter(part => part && typeof part.text === 'string' && /^(output_)?text$/.test(part.type))
    .map(part => part.text).join('\n').trim();
  return text.length > 0 ? text : null;
}

/** Last assistant message in a JSONL transcript, or null when there is none to find. */
export function lastAssistantMessage(transcriptPath, fsImpl = fs) {
  if (typeof transcriptPath !== 'string' || transcriptPath.length === 0) return null;
  let fd;
  try {
    // One descriptor for the size check and the read: checked by path, the file could be
    // swapped for a larger one between the two calls.
    fd = fsImpl.openSync(transcriptPath, 'r');
    if (fsImpl.fstatSync(fd).size > MAX_TRANSCRIPT_BYTES) return null;
    const lines = fsImpl.readFileSync(fd, 'utf8').split('\n');
    for (let i = lines.length - 1; i >= 0; i--) {
      if (lines[i].trim().length === 0) continue;
      let entry;
      try { entry = JSON.parse(lines[i]); } catch { continue; }
      const text = assistantText(entry);
      if (text !== null) return text;
    }
  } catch {
    // Unreadable transcript: the generic message is still a correct notification.
  } finally {
    if (fd !== undefined) { try { fsImpl.closeSync(fd); } catch { /* nothing left to release */ } }
  }
  return null;
}

function clip(text) {
  // Never end on a lone high surrogate; the relays re-encode the text as UTF-8.
  return text.slice(0, MAX_MESSAGE_CHARS).replace(/[\uD800-\uDBFF]$/, '');
}

/**
 * The notification body for this stop, or null when nothing should be sent. Pure, so the
 * tests can drive every branch without a server.
 */
export function decide(payload, env = process.env, fsImpl = fs) {
  if (!isInteractive(env)) return null;
  if (isHookContinuation(payload)) return null;
  if (isCodexHelperThread(payload)) return null;
  // Cursor reports why it stopped. An abort comes from the keyboard, so someone is already at
  // the terminal; an error still leaves the agent waiting, and the owner should hear of it.
  if (payload.status === 'aborted') return null;

  const own = typeof payload.last_assistant_message === 'string'
    ? payload.last_assistant_message.trim() : '';
  const message = own.length > 0 ? own
    : lastAssistantMessage(payload.transcript_path, fsImpl) ?? DEFAULT_MESSAGE;
  return { event: 'question', message: clip(message) };
}

export async function send(body, env = process.env, fetchImpl = fetch) {
  const base = env.AGENTHUB_CALLBACK_URL;
  const token = env.AGENTHUB_CALLBACK_TOKEN;
  if (!base || !token) return false;
  const url = new URL(`${base.replace(/\/+$/, '')}/notify`);
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return false;

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), CALLBACK_TIMEOUT_MS);
  try {
    const response = await fetchImpl(url, {
      method: 'POST',
      headers: { 'X-Agent-Token': token, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      redirect: 'error',
      signal: controller.signal
    });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timeout);
  }
}

/** Entry point. Exported because Codex reaches it through a wrapper in its managed
 * hooks directory rather than by running this file. Prints nothing: Codex rejects plain
 * text on Stop, and an empty stdout is "no decision" for both agents. */
export async function cli() {
  const raw = await readStdin();
  let payload = {};
  try { payload = raw.trim() ? JSON.parse(raw) : {}; } catch { payload = {}; }
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) payload = {};

  const body = decide(payload);
  if (body) await send(body);
}

// A hook that throws blocks the agent for no reason, so every failure path here is silent.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await cli().catch(() => {});
}
