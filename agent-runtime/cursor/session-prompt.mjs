import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { writeSystemPromptFile } from '../common/system-prompt-file.mjs';

const RULE_RELATIVE_PATH = path.join('.cursor', 'rules', 'agenthub-session.mdc');

/**
 * Gives a Cursor session the caller's extra system prompt.
 *
 * Cursor is the one runtime with nowhere else to put it. Its CLI has no system-prompt option, and
 * its rule loader is rooted at the workspace: `.cursor/rules/*.mdc`, `AGENTS.md`, `CLAUDE.md` and
 * `.cursorrules` are all resolved against the workspace root, with no home-directory or
 * environment equivalent (checked against the shipped CLI bundle, 2026.09.28). So the prompt has
 * to be a file in the working directory.
 *
 * `.cursor/rules/` and not `AGENTS.md`, because a rule file is additive — a repository's own
 * AGENTS.md keeps being read — whereas writing AGENTS.md would overwrite instructions the
 * repository ships. `alwaysApply: true` is what makes the loader treat a rule as global rather
 * than glob- or description-matched, so the text is in context for every turn instead of only
 * when Cursor decides it looks relevant.
 */
function ruleDocument(systemPrompt) {
  return [
    '---',
    'alwaysApply: true',
    'description: Standing instructions for this AgentHub session.',
    '---',
    '',
    systemPrompt
  ].join('\n');
}

/**
 * Keeps the rule file out of `git status` by naming it in `.git/info/exclude`.
 *
 * With a single repository the session's working directory *is* the clone, so the rule file lands
 * inside a tree the agent is about to work in. An untracked file there is not harmless: an agent
 * running `git add -A` would commit AgentHub's plumbing into the user's branch. `.git/info/exclude`
 * is the right lever because it is per-clone and untracked itself — unlike `.gitignore`, which is
 * a tracked file whose modification would be the very problem it is meant to prevent.
 */
function excludeFromGit(workdir, relativePath) {
  const gitDir = path.join(workdir, '.git');
  // A `.git` file rather than a directory means a worktree or submodule, where info/exclude lives
  // elsewhere; not worth resolving, and the pod only ever clones plain repositories.
  if (!fs.existsSync(gitDir) || !fs.statSync(gitDir).isDirectory()) return false;
  const excludePath = path.join(gitDir, 'info', 'exclude');
  const entry = `/${relativePath.split(path.sep).join('/')}`;
  let current = '';
  // Read straight away rather than checking first: between an existence check and the read
  // the path can be something else, and what gets appended below is based on this content.
  try { current = fs.readFileSync(excludePath, 'utf8'); } catch { current = ''; }
  if (current.split(/\r?\n/).includes(entry)) return true;
  fs.mkdirSync(path.dirname(excludePath), { recursive: true });
  fs.appendFileSync(excludePath, `${current.endsWith('\n') || current === '' ? '' : '\n'}${entry}\n`);
  return true;
}

export function writeCursorSystemPrompt(workdir, systemPrompt) {
  const text = typeof systemPrompt === 'string' ? systemPrompt.trim() : '';
  const rulePath = path.join(workdir, RULE_RELATIVE_PATH);
  if (!text) {
    fs.rmSync(rulePath, { force: true });
    return null;
  }
  writeSystemPromptFile(rulePath, ruleDocument(text));
  excludeFromGit(workdir, RULE_RELATIVE_PATH);
  return rulePath;
}

export { RULE_RELATIVE_PATH, ruleDocument, excludeFromGit };

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeCursorSystemPrompt(process.argv[2], process.env.AGENTHUB_SYSTEM_PROMPT);
