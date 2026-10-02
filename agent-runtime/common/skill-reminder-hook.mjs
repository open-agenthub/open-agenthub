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
const CHANGING_TOOLS = /^(edit|write|multiedit|notebookedit|bash|powershell|shell|local_shell|apply_patch|exec_command|write_file|run_terminal_cmd|create_file|str_replace.*)$/i;
const UPLOAD_TOOL = /upload_skill$/;

// The shell tools, whose name says nothing about whether the turn changed anything: `git
// status` and `git commit` arrive as the same tool. Answering "is it deployed?" with three
// kubectl gets used to count as work and earn a reminder, which is the noise this hook was
// supposed to stay out of.
const SHELL_TOOLS = /^(bash|powershell|shell|local_shell|exec_command|run_terminal_cmd)$/i;

// Programs that only ever report.
const READ_ONLY_PROGRAMS = new Set([
  'ls', 'cat', 'head', 'tail', 'wc', 'grep', 'rg', 'egrep', 'fgrep', 'find', 'file', 'stat',
  'du', 'df', 'pwd', 'cd', 'which', 'whoami', 'date', 'env', 'printenv', 'echo', 'printf',
  'sort', 'uniq', 'cut', 'tr', 'column', 'basename', 'dirname', 'realpath', 'readlink',
  'true', 'false', 'sleep', 'cmp', 'diff', 'jq', 'yq', 'od', 'xxd', 'type', 'command', 'test'
]);

// Programs whose first subcommand decides it. The nested sets are the second level, for the
// ones where it takes two words to tell `gh pr view` from `gh pr create`.
const READ_ONLY_SUBCOMMANDS = {
  git: {
    status: true, log: true, diff: true, show: true, branch: true, remote: true,
    describe: true, 'rev-parse': true, 'rev-list': true, 'ls-files': true, 'ls-remote': true,
    'merge-base': true, 'cat-file': true, blame: true, shortlog: true, fetch: true,
    worktree: new Set(['list'])
  },
  kubectl: {
    get: true, describe: true, logs: true, top: true, version: true, explain: true,
    'api-resources': true, 'api-versions': true, 'cluster-info': true,
    config: new Set(['view', 'current-context', 'get-contexts', 'get-clusters', 'get-users'])
  },
  helm: {
    list: true, status: true, show: true, search: true, version: true, template: true,
    history: true, diff: true, get: true
  },
  docker: {
    ps: true, images: true, version: true, info: true, history: true, logs: true,
    inspect: true, manifest: new Set(['inspect']), image: new Set(['inspect', 'ls'])
  },
  gh: {
    pr: new Set(['list', 'view', 'checks', 'diff', 'status']),
    run: new Set(['list', 'view', 'watch']),
    release: new Set(['list', 'view']),
    repo: new Set(['view']),
    issue: new Set(['list', 'view']),
    auth: new Set(['status'])
  }
};

// Test runs write into obj/ and node_modules/.cache, never into the work itself.
const TEST_RUNNERS = new Set(['dotnet', 'npm', 'npx', 'node', 'pnpm', 'yarn', 'pwsh']);
const TEST_FLAGS = new Set(['--test', '--version', '-v', '-V']);
// PowerShell's reporting verbs.
const READ_ONLY_CMDLET = /^(?:Get|Select|Measure|Compare|Resolve|Test|Format|Out|Where|Sort|ConvertFrom|Write)-\w+$/i;

// Flags whose value is a separate token, so the value is not mistaken for the subcommand:
// in `kubectl --context kube01 -n agenthub get pods`, kube01 and agenthub are not it.
const VALUE_FLAGS = new Set([
  '-C', '-c', '--git-dir', '--work-tree',
  '--context', '--kube-context', '-n', '--namespace', '--kubeconfig', '-o', '--output',
  '--server', '--token', '--as', '-H', '--host', '--config', '-R', '--repo', '-f', '--file'
]);

/** First token that is neither a flag nor a flag's value, from `from` onwards. */
function firstWord(tokens, from) {
  for (let i = from; i < tokens.length; i++) {
    const token = tokens[i];
    if (!token.startsWith('-')) return token;
    // `--flag=value` carries its own value; a bare one consumes the next token.
    if (!token.includes('=') && VALUE_FLAGS.has(token)) i++;
  }
  return null;
}

/**
 * True when this one command only reports.
 *
 * Tokenised rather than matched as a whole, because the subcommand is a position, not a
 * substring: `git commit -m "fix status"` has the word status in it and is emphatically not
 * read-only. Recognising the reporting commands rather than listing the writing ones is also
 * deliberate — an unrecognised command counts as work, so a `./deploy.sh` nobody anticipated
 * still earns its reminder, and a gap here costs one reminder too many, not a lost lesson.
 */
function isReadOnlySegment(segment) {
  const tokens = segment.split(/\s+/).filter(t => t.length > 0);
  if (tokens.length === 0) return false;
  const program = tokens[0].replace(/^.*[/\\]/, '').replace(/\.(?:exe|cmd|sh)$/i, '');

  if (READ_ONLY_PROGRAMS.has(program)) return true;
  if (READ_ONLY_CMDLET.test(program)) return true;
  // -n prints; without it sed takes -i and edits in place.
  if (program === 'sed') return tokens.includes('-n');
  if (TEST_RUNNERS.has(program)) {
    return firstWord(tokens, 1) === 'test' || tokens.slice(1).some(t => TEST_FLAGS.has(t));
  }
  // `git tag` lists; `git tag -a` creates one. Same for anything else that reports until
  // given a flag, so a non-listing flag disqualifies it.
  if (program === 'git' && firstWord(tokens, 1) === 'tag') {
    return tokens.slice(2).every(t => !t.startsWith('-') || t === '-l' || t === '--list');
  }
  if (program === 'git' && firstWord(tokens, 1) === 'config') {
    return tokens.some(t => t.startsWith('--get'));
  }

  const rules = READ_ONLY_SUBCOMMANDS[program];
  if (!rules) return false;
  const sub = firstWord(tokens, 1);
  if (sub === null) return false;
  const rule = rules[sub];
  if (rule === true) return true;
  if (rule instanceof Set) {
    const second = firstWord(tokens, tokens.indexOf(sub) + 1);
    return second !== null && rule.has(second);
  }
  return false;
}

/** The command behind a shell tool call, whatever the agent calls that field. */
function commandOf(payload) {
  const input = payload.tool_input ?? payload.toolInput ?? payload.input ?? payload;
  const value = input?.command ?? input?.cmd ?? input?.script ?? payload.command;
  if (Array.isArray(value)) return value.map(String).join(' ');
  return typeof value === 'string' ? value : null;
}

/**
 * True when every part of the command only reports. Split on the separators, because one
 * writing step anywhere makes the whole line count — `git status && git commit` is work.
 * A redirection writes a file whatever the command before it does.
 */
export function isReadOnlyCommand(command) {
  if (typeof command !== 'string' || command.trim().length === 0) return false;
  // A redirection writes a file whatever the command in front of it does. `2>&1` and `>/dev/null`
  // do not, and are too common in a plain lookup to disqualify it.
  if (/>\s*(?!&\d|\/dev\/null)\S/.test(command.replace(/\d>/g, '>')) || /\btee\b/.test(command))
    return false;
  const segments = command.split(/\|\||&&|[|;\n]/).map(s => s.trim()).filter(s => s.length > 0);
  if (segments.length === 0) return false;
  // Every part has to report: one writing step anywhere makes the whole line count.
  return segments.every(segment => isReadOnlySegment(
    // Drop a leading subshell paren and env assignments, so `TMPDIR=x ls` reads as `ls`.
    segment.replace(/^[({\s]*/, '').replace(/^(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)+/, '')));
}

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
  if (!uploaded && tool !== null && SHELL_TOOLS.test(tool) && isReadOnlyCommand(commandOf(payload)))
    return null;

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
