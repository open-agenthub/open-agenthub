#!/usr/bin/env node
// End-of-turn reminder to put reusable knowledge into the skill library.
//
// The skill-library MCP server already tells the agent to upload what is worth keeping, and
// agents do read it — at the start. At the end of a long task the instruction is far behind
// and the turn ends without it, which is exactly when there is something to save. A hook at
// the end of the turn is the only place that cannot be forgotten.
//
// One script for three agents, because all three have the same hook at the same moment and
// differ only in wording:
//   Claude Code  settings.json  hooks.Stop        -> {"decision":"block","reason":…}
//   Codex        requirements.toml [[hooks.Stop]] -> {"decision":"block","reason":…}
//   Cursor       hooks.json     hooks.stop        -> {"followup_message":…}
//
// Two modes:
//   --mark [kind]   record that this turn did something (PostToolUse / afterFileEdit)
//   (no argument)   decide at the end of the turn whether to remind
//
// The marker is what keeps the reminder off a turn that only answered a question: a reply
// with no edit and no command behind it has nothing reusable in it by definition. Without a
// marker the hook stays silent — being quiet when unsure is the cheaper mistake, since the
// alternative interrupts a plain answer with a chore.

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const REMINDER =
  'Before you finish: is there a reusable procedure, a non-obvious solution, or a helper '
  + 'script in what you just did that belongs in the skill library? If yes, save it with '
  + 'upload_skill (or update the existing skill — pass a local path, the files do not have to '
  + 'go through your context) and mention it in one line. If not, say so in one line and stop.';

/** Marker directory: TMPDIR is the one place a session pod owns (read-only root filesystem). */
function markerDir(env) {
  return env.TMPDIR || env.TEMP || os.tmpdir();
}

/**
 * One key per turn-producing conversation. Every agent names it differently, and a session
 * without an id at all still gets a stable key from its working directory — two sessions in
 * the same directory sharing a marker is harmless, both did work.
 */
function sessionKey(payload, env) {
  const id = payload.session_id || payload.conversation_id || payload.turn_id
    || payload.generation_id || env.AGENTHUB_SESSION_ID || payload.cwd || process.cwd();
  return crypto.createHash('sha256').update(String(id)).digest('hex').slice(0, 16);
}

function markerPath(payload, env, kind) {
  return path.join(markerDir(env), `.agenthub-skill-${kind}-${sessionKey(payload, env)}`);
}

async function readStdin() {
  const chunks = [];
  let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    // A hook payload is small; a transcript accidentally piped in is not worth reading.
    if (size > 1_000_000) break;
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

/** Cursor's stop hook speaks a different dialect and counts its own follow-ups. */
function isCursor(payload) {
  return typeof payload.conversation_id === 'string' && payload.stop_hook_active === undefined;
}

/** True when this stop is itself the result of a previous reminder. */
function alreadyContinued(payload) {
  if (payload.stop_hook_active === true) return true;
  return Number(payload.loop_count) > 0;
}

export function decide(payload, env = process.env, fsImpl = fs) {
  if (env.AGENTHUB_SKILL_REMINDER === '0') return null;
  if (alreadyContinued(payload)) return null;
  // An aborted or errored turn is not a finished piece of work to save.
  if (payload.status !== undefined && payload.status !== 'completed') return null;

  const work = markerPath(payload, env, 'work');
  const uploaded = markerPath(payload, env, 'uploaded');
  const didWork = fsImpl.existsSync(work);
  const didUpload = fsImpl.existsSync(uploaded);
  // Cleared either way: the next turn has to earn its own reminder.
  for (const file of [work, uploaded]) {
    try { fsImpl.rmSync(file, { force: true }); } catch { /* best effort */ }
  }
  if (!didWork || didUpload) return null;

  return isCursor(payload)
    ? { followup_message: REMINDER }
    : { decision: 'block', reason: REMINDER };
}

// Tools that change something, across the three agents' naming. Reading a file, listing a
// directory or searching does not make a turn worth a reminder; editing one or running a
// command does. Codex's managed hooks cannot express a matcher per tool, so the filter has
// to be here as well as in the hook configuration.
const CHANGING_TOOLS = /^(edit|write|multiedit|notebookedit|bash|shell|local_shell|apply_patch|exec_command|write_file|run_terminal_cmd|create_file|str_replace.*)$/i;
const UPLOAD_TOOL = /upload_skill$/;

function toolName(payload) {
  const name = payload.tool_name ?? payload.toolName ?? payload.tool ?? payload.name;
  return typeof name === 'string' ? name : null;
}

export function mark(payload, kind = 'work', env = process.env, fsImpl = fs) {
  const tool = toolName(payload);
  // An upload is recognised wherever it shows up, so a turn that already saved its lesson
  // is never asked about it.
  const uploaded = kind === 'uploaded' || (tool !== null && UPLOAD_TOOL.test(tool));
  if (!uploaded && tool !== null && !CHANGING_TOOLS.test(tool)) return null;

  const file = markerPath(payload, env, uploaded ? 'uploaded' : 'work');
  try {
    fsImpl.writeFileSync(file, '', { mode: 0o600 });
  } catch {
    // A marker that cannot be written costs a reminder, never a failed tool call.
  }
  return file;
}

/** Entry point. Exported because Codex reaches it through a wrapper in its managed
 * hooks directory rather than by running this file. */
export async function cli(argv = process.argv) {
  const raw = await readStdin();
  let payload = {};
  try { payload = raw.trim() ? JSON.parse(raw) : {}; } catch { payload = {}; }

  const markIndex = argv.indexOf('--mark');
  if (markIndex >= 0) {
    mark(payload, argv[markIndex + 1]);
    return;
  }
  const decision = decide(payload);
  if (decision) process.stdout.write(JSON.stringify(decision));
}

// A hook that throws blocks the agent for no reason, so every failure path here is silent.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await cli().catch(() => {});
}
