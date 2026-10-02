'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const RULE = path.join('.cursor', 'rules', 'agenthub-session.mdc');

function tempWorkdir({ git = false } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-cursor-prompt-'));
  if (git) fs.mkdirSync(path.join(dir, '.git'), { recursive: true });
  return dir;
}

test('the caller system prompt becomes an always-applied rule, not an overwritten AGENTS.md', async () => {
  const { writeCursorSystemPrompt } = await import('../../cursor/session-prompt.mjs');
  const workdir = tempWorkdir();

  const written = writeCursorSystemPrompt(workdir, 'You review, you do not commit.');

  assert.equal(written, path.join(workdir, RULE));
  const body = fs.readFileSync(written, 'utf8');
  // alwaysApply is what makes the loader treat the rule as global instead of glob-matched, so the
  // text is in context every turn rather than only when Cursor judges it relevant.
  assert.match(body, /^---\nalwaysApply: true\n/);
  assert.match(body, /You review, you do not commit\./);
  // A repository's own AGENTS.md must keep applying; a rule file adds, overwriting would replace.
  assert.equal(fs.existsSync(path.join(workdir, 'AGENTS.md')), false);
});

test('the rule file is hidden from git so an agent cannot commit AgentHub plumbing', async () => {
  const { writeCursorSystemPrompt } = await import('../../cursor/session-prompt.mjs');
  const workdir = tempWorkdir({ git: true });

  writeCursorSystemPrompt(workdir, 'be terse');
  const excludePath = path.join(workdir, '.git', 'info', 'exclude');
  const exclude = fs.readFileSync(excludePath, 'utf8');

  // Anchored and forward-slashed, so it reads the same on every platform git sees it on.
  assert.match(exclude, /^\/\.cursor\/rules\/agenthub-session\.mdc$/m);

  // .gitignore is tracked, so writing there would be the very modification this avoids.
  assert.equal(fs.existsSync(path.join(workdir, '.gitignore')), false);
});

test('the git exclude entry is not duplicated when the session restarts', async () => {
  const { writeCursorSystemPrompt } = await import('../../cursor/session-prompt.mjs');
  const workdir = tempWorkdir({ git: true });
  const excludePath = path.join(workdir, '.git', 'info', 'exclude');
  fs.mkdirSync(path.dirname(excludePath), { recursive: true });
  fs.writeFileSync(excludePath, '# git ls-files --others --exclude-from=.git/info/exclude\n');

  writeCursorSystemPrompt(workdir, 'be terse');
  writeCursorSystemPrompt(workdir, 'be terse');
  writeCursorSystemPrompt(workdir, 'and specific');

  const lines = fs.readFileSync(excludePath, 'utf8').split('\n')
    .filter((line) => line === '/.cursor/rules/agenthub-session.mdc');
  assert.equal(lines.length, 1);
  // The pre-existing comment must survive: info/exclude may hold the user's own entries.
  assert.match(fs.readFileSync(excludePath, 'utf8'), /^# git ls-files/m);
});

test('a workdir that is not a git clone still gets the rule', async () => {
  const { writeCursorSystemPrompt } = await import('../../cursor/session-prompt.mjs');
  // Multi-repo sessions run in /workspace, which is not itself a repository.
  const workdir = tempWorkdir();

  assert.equal(writeCursorSystemPrompt(workdir, 'be terse'), path.join(workdir, RULE));
  assert.equal(fs.existsSync(path.join(workdir, '.git')), false);
});

test('a .git file rather than a directory is left alone', async () => {
  const { excludeFromGit } = await import('../../cursor/session-prompt.mjs');
  const workdir = tempWorkdir();
  // A worktree or submodule points elsewhere; appending to a path under a regular file throws.
  fs.writeFileSync(path.join(workdir, '.git'), 'gitdir: /elsewhere/.git/worktrees/x\n');

  assert.equal(excludeFromGit(workdir, RULE), false);
});

test('an empty system prompt removes a rule left by an earlier run', async () => {
  const { writeCursorSystemPrompt } = await import('../../cursor/session-prompt.mjs');
  const workdir = tempWorkdir();
  const rulePath = path.join(workdir, RULE);

  for (const value of ['', '   ', undefined]) {
    fs.mkdirSync(path.dirname(rulePath), { recursive: true });
    fs.writeFileSync(rulePath, 'rules from a previous run\n');
    assert.equal(writeCursorSystemPrompt(workdir, value), null);
    assert.equal(fs.existsSync(rulePath), false);
  }
});

test('the cursor entrypoint writes the rule before starting the session agent', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'cursor', 'entrypoint.sh'), 'utf8');
  const sessionPrompt = entrypoint.indexOf('cursor/session-prompt.mjs');
  const exec = entrypoint.indexOf('exec node "$RUNTIME/common/server.js"');

  assert.ok(sessionPrompt >= 0 && exec > sessionPrompt);
  assert.match(entrypoint, /node "\$RUNTIME\/cursor\/session-prompt\.mjs" "\$CURSOR_WORKDIR"/);
  assert.match(entrypoint, /CURSOR_WORKDIR="\$\{AGENTHUB_WORKDIR:-\/workspace\}"/);
});
