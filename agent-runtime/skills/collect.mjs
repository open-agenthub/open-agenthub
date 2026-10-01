// Reading a skill off the pod's disk and writing one back to it.
//
// This is the half of the skills proxy that never touches the network. It exists so a
// helper script does not have to travel through the agent's context to reach the library:
// an 18 KB PowerShell file quoted into a tool call costs thousands of tokens on the way up
// and the same again on the way down, which is why uploads of anything sizeable were
// simply skipped. Here the agent names a path and the bytes go pod → hub directly.
//
// The limits mirror the backend's (LibraryValidation): exceeding them server-side rejects
// the whole upload after it was read and sent, so the same ceilings are applied before.

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFile } from 'node:child_process';
import { createRequire } from 'node:module';

const runtimeRequire = createRequire(import.meta.url);
const { safeFilePath, MAX_CONTENT, MAX_FILES } = runtimeRequire('../common/skills.js');

const MAX_TOTAL_CHARS = 500_000;
// An archive is read into memory to be unpacked; the files inside it can never add up to
// more than MAX_TOTAL_CHARS anyway, so anything much larger is not a skill.
const MAX_ARCHIVE_BYTES = 10 * 1024 * 1024;
const MAX_SCAN_DEPTH = 3;
const ARCHIVE_SUFFIXES = ['.tar.gz', '.tgz'];

export function isArchive(candidate) {
  const lower = candidate.toLowerCase();
  return ARCHIVE_SUFFIXES.some(suffix => lower.endsWith(suffix));
}

function run(file, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = execFile(file, args, { ...options, encoding: 'buffer' }, (error, stdout, stderr) => {
      if (error) {
        // All of tar's complaint, not just the last line: the last line is usually
        // "Error is not recoverable", and the line before it is the one that says why.
        const detail = stderr?.length ? stderr.toString('utf8').trim().replace(/\s*\n\s*/g, ' — ')
          : error.message;
        reject(new Error(`skills_archive_failed: ${detail}`));
        return;
      }
      resolve(stdout);
    });
    if (options.input !== undefined) {
      child.stdin.end(options.input);
    }
  });
}

/** Reads a text file, or null when it is absent, too large, or not text. */
function readText(file, fsImpl) {
  let stat;
  try { stat = fsImpl.statSync(file); } catch { return null; }
  // A byte count is the only cheap pre-check; the character count is what the backend
  // limits, and multi-byte text can only ever be shorter than its bytes.
  if (!stat.isFile() || stat.size > MAX_CONTENT * 4) return null;
  const bytes = fsImpl.readFileSync(file);
  if (bytes.includes(0)) return null; // binary
  const text = bytes.toString('utf8');
  return text.length > MAX_CONTENT ? null : text;
}

/**
 * Walks a skill directory into { content, files }.
 *
 * Paths that the backend would reject (too deep, odd characters, dot-files) are reported
 * as skipped rather than silently dropped: the agent has to learn that its scripts/ sub
 * sub directory did not make it, or it will believe the skill is complete.
 */
export function collectDirectory(dir, deps = {}) {
  const fsImpl = deps.fs || fs;
  const files = [];
  const skipped = [];
  let total = 0;

  const walk = (current, prefix, depth) => {
    const entries = fsImpl.readdirSync(current, { withFileTypes: true })
      .sort((a, b) => a.name.localeCompare(b.name));
    for (const entry of entries) {
      const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        if (depth >= MAX_SCAN_DEPTH) {
          skipped.push(`${relative}/ (nested deeper than ${MAX_SCAN_DEPTH} levels)`);
          continue;
        }
        walk(path.join(current, entry.name), relative, depth + 1);
        continue;
      }
      if (relative === 'SKILL.md') continue; // the skill itself, handled by the caller
      if (!entry.isFile()) {
        skipped.push(`${relative} (not a regular file)`);
        continue;
      }
      if (!safeFilePath(relative)) {
        skipped.push(`${relative} (path not allowed next to SKILL.md)`);
        continue;
      }
      if (files.length >= MAX_FILES) {
        skipped.push(`${relative} (more than ${MAX_FILES} files)`);
        continue;
      }
      const content = readText(path.join(current, entry.name), fsImpl);
      if (content === null) {
        skipped.push(`${relative} (binary or larger than ${MAX_CONTENT} characters)`);
        continue;
      }
      if (total + content.length > MAX_TOTAL_CHARS) {
        skipped.push(`${relative} (would exceed the ${MAX_TOTAL_CHARS} character total)`);
        continue;
      }
      total += content.length;
      files.push({ path: relative, content });
    }
  };
  walk(dir, '', 1);

  return { content: readText(path.join(dir, 'SKILL.md'), fsImpl), files, skipped, chars: total };
}

/**
 * Resolves what the agent passed as `path` into a skill payload.
 *
 * Three shapes, because all three are things an agent actually has on disk at the end of a
 * task: a skill directory it assembled, a single helper script, and a tar.gz it already
 * built. `content` is the SKILL.md from the tool call and wins over a SKILL.md on disk only
 * when there is none — the file on disk is the more deliberate of the two.
 */
export async function collectSource(source, deps = {}) {
  const fsImpl = deps.fs || fs;
  const resolved = path.resolve(source);
  let stat;
  try { stat = fsImpl.statSync(resolved); } catch { throw new Error(`skills_path_not_found: ${source}`); }

  if (stat.isDirectory()) {
    return { ...collectDirectory(resolved, deps), source: resolved, kind: 'directory' };
  }
  if (!stat.isFile()) throw new Error(`skills_path_not_a_file: ${source}`);

  if (isArchive(resolved)) {
    if (stat.size > MAX_ARCHIVE_BYTES) throw new Error(`skills_archive_too_large: ${source}`);
    const temp = fsImpl.mkdtempSync(path.join(deps.tmpdir || os.tmpdir(), 'agenthub-skill-'));
    try {
      // No path reaches tar: the archive comes in on stdin and the destination is the
      // child's working directory. tar implementations differ in how they read a path
      // argument (one takes "host:path" for a remote archive, another rewrites the
      // separators), and none of them has an opinion about cwd.
      await (deps.run || run)('tar', ['xzf', '-'],
        { cwd: temp, input: fsImpl.readFileSync(resolved) });
      // An archive made from the skill directory itself unpacks into a single directory;
      // one made with `tar czf x.tgz -C skill .` unpacks flat. Both are normal.
      const entries = fsImpl.readdirSync(temp, { withFileTypes: true });
      const root = entries.length === 1 && entries[0].isDirectory()
        ? path.join(temp, entries[0].name)
        : temp;
      return { ...collectDirectory(root, deps), source: resolved, kind: 'archive' };
    } finally {
      fsImpl.rmSync(temp, { recursive: true, force: true });
    }
  }

  const name = path.basename(resolved);
  if (name === 'SKILL.md') {
    const content = readText(resolved, fsImpl);
    if (content === null) throw new Error(`skills_file_not_text: ${source}`);
    return { content, files: [], skipped: [], chars: 0, source: resolved, kind: 'skill-md' };
  }
  if (!safeFilePath(name)) throw new Error(`skills_file_name_not_allowed: ${name}`);
  const content = readText(resolved, fsImpl);
  if (content === null) throw new Error(`skills_file_not_text: ${source}`);
  return {
    content: null,
    files: [{ path: name, content }],
    skipped: [],
    chars: content.length,
    source: resolved,
    kind: 'file'
  };
}

/**
 * Unpacks a skill bundle (SKILL.md plus its files) into a directory and reports the paths.
 *
 * The archive comes from the hub, whose own validation already forbids absolute paths, ..
 * segments and anything deeper than three levels, so extraction cannot write outside the
 * target. `tar t` first because the two tar implementations in play disagree about which
 * stream -v writes names to.
 */
export async function writeBundle(outDir, archive, deps = {}) {
  const fsImpl = deps.fs || fs;
  const runImpl = deps.run || run;
  const target = path.resolve(outDir);
  fsImpl.mkdirSync(target, { recursive: true });

  const listing = await runImpl('tar', ['tzf', '-'], { input: archive });
  const names = listing.toString('utf8').split('\n')
    .map(line => line.trim())
    .filter(line => line.length > 0 && !line.endsWith('/'));
  // cwd rather than -C, for the same reason as in collectSource: no path argument for a
  // tar implementation to reinterpret.
  await runImpl('tar', ['xzf', '-'], { cwd: target, input: archive });

  return { dir: target, written: names.map(name => path.join(target, name)) };
}
