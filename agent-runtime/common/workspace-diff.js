'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const { execFile } = require('node:child_process');
const LIMITS = Object.freeze({ repositories: 32, files: 200, patchBytes: 65536, totalBytes: 524288 });
const SKIP = new Set(['.git', 'node_modules', '.agenthub', '.cache', 'vendor', 'bin', 'obj']);

function git(cwd, args, maxBuffer = 1024 * 1024) {
  return new Promise((resolve, reject) => execFile('git', ['--no-optional-locks', '-c',
    'core.fsmonitor=false', '-c', 'core.quotePath=false', ...args], {
    cwd, timeout: 3000, maxBuffer, encoding: 'utf8',
    env: { ...process.env, GIT_TERMINAL_PROMPT: '0', GIT_EXTERNAL_DIFF: '', GIT_PAGER: 'cat' }
  }, (error, stdout) => { if (error) { error.stdout = stdout; reject(error); } else resolve(stdout); }));
}

function createWorkspaceDiff(root = '/workspace') {
  const cache = new Map();
  return async function read(comparison = 'base') {
    if (!['base', 'head'].includes(comparison)) throw new Error('invalid_comparison');
    const cached = cache.get(comparison);
    if (cached && (cached.pending || Date.now() - cached.at < 2000)) return cached.promise;
    const promise = collect(root, comparison);
    const entry = { at: Date.now(), promise, pending: true };
    cache.set(comparison, entry);
    try { const result = await promise; entry.pending = false; entry.at = Date.now(); return result; } catch (error) { cache.delete(comparison); throw error; }
  };
}

async function collect(root, comparison) {
  const result = { comparison, repositories: [], truncated: false, limits: LIMITS };
  const pending = [[root, 0]];
  let directories = 0;
  const deadline = Date.now() + 10000;
  let filesLeft = LIMITS.files;
  let bytesLeft = LIMITS.totalBytes;
  while (pending.length) {
    if (Date.now() > deadline) { result.truncated = true; break; }
    const [directory, depth] = pending.shift();
    if (++directories > 10000) { result.truncated = true; break; }
    let entries;
    try { entries = await fs.readdir(directory, { withFileTypes: true }); } catch { continue; }
    if (entries.some(entry => entry.name === '.git' && !entry.isSymbolicLink())) {
      if (result.repositories.length >= LIMITS.repositories) { result.truncated = true; break; }
      const repo = { path: path.relative(root, directory) || '.', base: '', files: [], error: null };
      result.repositories.push(repo);
      try {
        let base = 'HEAD';
        if (comparison === 'base') {
          for (const ref of ['refs/remotes/origin/HEAD', 'refs/remotes/origin/main', 'refs/heads/main']) {
            try { base = (await git(directory, ['merge-base', 'HEAD', ref])).trim(); break; } catch { /* A new/local repository may have no remote base. */ }
          }
        }
        try { repo.base = (await git(directory, ['rev-parse', '--verify', base])).trim(); }
        catch {
          // An unborn repository has no HEAD; the empty tree makes its files reviewable too.
          repo.base = (await git(directory, ['hash-object', '-t', 'tree', '/dev/null'])).trim();
        }
        const tracked = (await git(directory, ['diff', '--name-only', '-z', '--no-renames', repo.base, '--'])).split('\0').filter(Boolean);
        const untracked = (await git(directory, ['ls-files', '--others', '--exclude-standard', '-z'])).split('\0').filter(Boolean);
        const added = new Set(untracked);
        for (const name of [...new Set([...tracked, ...untracked])]) {
          if (Date.now() > deadline || filesLeft-- <= 0) { result.truncated = true; break; }
          const file = { path: name, patch: '', omitted: null };
          repo.files.push(file);
          const absolute = path.join(directory, name);
          try {
            const stat = await fs.lstat(absolute).catch(error => error.code === 'ENOENT' ? null : Promise.reject(error));
            if (stat?.isSymbolicLink() && added.has(name)) { file.omitted = 'symlink'; continue; }
            if (stat?.isFile() && stat.size > LIMITS.patchBytes) { file.omitted = 'large'; continue; }
            if (bytesLeft <= 0) { file.omitted = 'limit'; result.truncated = true; continue; }
            const args = ['diff', '--no-ext-diff', '--no-textconv', '--no-renames', '--no-color', '--unified=3'];
            let patch;
            if (added.has(name)) {
              // no-index exits 1 for a difference; retain stdout without invoking a shell.
              try { patch = await git(directory, [...args, '--no-index', '--', '/dev/null', name], LIMITS.patchBytes); }
              catch (error) { if (error.code === 1 && typeof error.stdout === 'string') patch = error.stdout; else throw error; }
            } else patch = await git(directory, [...args, repo.base, '--', name], LIMITS.patchBytes);
            if (/^Binary files /m.test(patch)) file.omitted = 'binary';
            else if (Buffer.byteLength(patch) > bytesLeft) { file.omitted = 'limit'; result.truncated = true; }
            else { file.patch = patch; bytesLeft -= Buffer.byteLength(patch); }
          } catch (error) { file.omitted = error.code === 'ERR_CHILD_PROCESS_STDIO_MAXBUFFER' ? 'large' : 'unavailable'; }
        }
      } catch { repo.error = 'repository_unavailable'; }
    }
    for (const entry of entries) {
      if (!entry.isDirectory() || entry.isSymbolicLink() || SKIP.has(entry.name)) continue;
      if (depth >= 12) { result.truncated = true; continue; }
      if (pending.length + directories >= 10000) { result.truncated = true; continue; }
      pending.push([path.join(directory, entry.name), depth + 1]);
    }
  }
  return result;
}

module.exports = { createWorkspaceDiff, LIMITS };
