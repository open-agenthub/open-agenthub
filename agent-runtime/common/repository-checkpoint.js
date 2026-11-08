'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { execFile } = require('node:child_process');
const LIMITS = Object.freeze({ repositories: 32, manifestBytes: 65536, pathBytes: 1024,
  entries: 10000, names: 200, namesBytes: 16384, outputBytes: 1024 * 1024, networkMs: 60000 });

function git(cwd, args) {
  return new Promise((resolve, reject) => execFile('git', ['--no-optional-locks',
    '-c', 'core.fsmonitor=false', '-c', 'core.hooksPath=/dev/null',
    '-c', 'protocol.file.allow=never', '-c', 'protocol.ext.allow=never', ...args], {
    cwd, timeout: LIMITS.networkMs, maxBuffer: LIMITS.outputBytes, encoding: 'utf8',
    env: { ...process.env, GIT_TERMINAL_PROMPT: '0', GIT_CONFIG_NOSYSTEM: '1',
      GIT_CONFIG_COUNT: '0', GIT_EXTERNAL_DIFF: '', GIT_PAGER: 'cat' }
  }, (error, stdout) => error ? reject(new Error('repository_check_failed')) : resolve(stdout)));
}
function names(raw) {
  const values = raw.split('\0').filter(Boolean);
  if (values.length > LIMITS.entries) throw new Error('repository_entry_limit');
  return values;
}
function sample(values) {
  let bytes = 0;
  const retained = [];
  for (const value of values) {
    const size = Buffer.byteLength(value);
    if (retained.length === LIMITS.names || bytes + size > LIMITS.namesBytes) break;
    retained.push(value); bytes += size;
  }
  return { count: values.length, names: retained, omitted: values.length - retained.length };
}
function relative(value) {
  if (typeof value !== 'string' || !value || value === '.' || path.isAbsolute(value)
      || value.split('/').some(part => !part || part === '.' || part === '..')
      || /[\x00-\x1f\x7f\\]/.test(value) || Buffer.byteLength(value) > LIMITS.pathBytes)
    throw new Error('repository_path_invalid');
  return value;
}
function remote(value) {
  // Reuse configured destinations only; local transports can read arbitrary pod files.
  if (typeof value !== 'string' || /[\x00-\x20\x7f]/.test(value)) throw new Error('repository_remote_invalid');
  let url; try { url = new URL(value); } catch { throw new Error('repository_remote_invalid'); }
  if (!['https:', 'http:', 'ssh:'].includes(url.protocol) || url.password || url.search || url.hash
      || (url.protocol !== 'ssh:' && url.username)) throw new Error('repository_remote_invalid');
  return value;
}

async function inspectRepository(root, configured, index) {
  const destination = relative(configured.destination);
  const result = { index, destination, branch: null, commit: null, remoteBranch: null,
    remoteTip: null, trackedChanges: null, untracked: null, ignored: null, unpushed: null, error: null };
  try {
    const directory = path.join(root, destination);
    if (await fs.realpath(directory) !== directory || !(await fs.lstat(path.join(directory, '.git'))).isDirectory())
      throw new Error('repository_layout_unsupported');
    const top = (await git(directory, ['rev-parse', '--show-toplevel'])).trim();
    if (top !== directory) throw new Error('repository_layout_unsupported');
    result.branch = (await git(directory, ['symbolic-ref', '--quiet', '--short', 'HEAD'])).trim();
    if (Buffer.byteLength(result.branch) > LIMITS.pathBytes) throw new Error('repository_ref_limit');
    await git(directory, ['check-ref-format', '--branch', result.branch]);
    result.commit = (await git(directory, ['rev-parse', '--verify', 'HEAD^{commit}'])).trim();
    if (!/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(result.commit)) throw new Error('repository_commit_invalid');
    const tracked = names(await git(directory, ['diff', '--name-only', '-z', '--no-ext-diff', 'HEAD', '--']));
    const untracked = names(await git(directory, ['ls-files', '--others', '--exclude-standard', '-z']));
    const ignored = names(await git(directory, ['ls-files', '--others', '--ignored', '--exclude-standard', '-z']));
    result.trackedChanges = tracked.length; result.untracked = untracked.length; result.ignored = sample(ignored);
    const files = names(await git(directory, ['ls-files', '-z']));
    if (files.some(name => name === '.gitmodules' || name.endsWith('/.gitmodules')))
      throw new Error('repository_submodules_unsupported');
    const attrs = await git(directory, ['ls-files', '--stage']);
    if (/^160000 /m.test(attrs)) throw new Error('repository_submodules_unsupported');
    // Attributes can select a large-file filter even when the pointer text looks like ordinary code.
    for (let start = 0; start < files.length; start += 100) {
      const attributes = await git(directory, ['check-attr', '-z', 'filter', '--', ...files.slice(start, start + 100)]);
      if (attributes.split('\0').some((value, offset) => offset % 3 === 2 && value === 'lfs'))
        throw new Error('repository_large_files_unsupported');
    }
    if (result.trackedChanges || result.untracked) throw new Error('repository_work_not_saved');
    const configuredUrl = remote(configured.url);
    const actualUrl = (await git(directory, ['remote', 'get-url', 'origin'])).trim();
    if (actualUrl !== configuredUrl) throw new Error('repository_remote_changed');
    const upstream = (await git(directory, ['for-each-ref', '--format=%(upstream:remotename)%00%(upstream:remoteref)',
      `refs/heads/${result.branch}`])).trim().split('\0');
    if (upstream[0] !== 'origin' || !upstream[1]?.startsWith('refs/heads/')) throw new Error('repository_upstream_required');
    result.remoteBranch = upstream[1];
    if (Buffer.byteLength(result.remoteBranch) > LIMITS.pathBytes) throw new Error('repository_ref_limit');
    await git(directory, ['check-ref-format', result.remoteBranch]);
    const verification = await fs.mkdtemp(path.join(os.tmpdir(), 'repo-check-'));
    try {
      await git(verification, ['init', '--bare', '.']);
      // A separate object database avoids trusting stale local remote refs or local objects.
      await git(verification, ['fetch', '--no-tags', '--', configuredUrl, result.remoteBranch]);
      result.remoteTip = (await git(verification, ['rev-parse', '--verify', 'FETCH_HEAD^{commit}'])).trim();
      try {
        await git(verification, ['merge-base', '--is-ancestor', result.commit, result.remoteTip]);
        result.unpushed = 0;
      } catch {
        result.unpushed = null;
        throw new Error('repository_commit_not_pushed');
      }
    } finally { await fs.rm(verification, { recursive: true, force: true }); }
  } catch (error) {
    result.error = /^repository_[a-z_]+$/.test(error.message) ? error.message : 'repository_check_failed';
  }
  return result;
}

async function inspect(root, configured) {
  root = await fs.realpath(root);
  if (!Array.isArray(configured) || configured.length === 0 || configured.length > LIMITS.repositories)
    throw new Error('repository_count_invalid');
  const destinations = configured.map(item => relative(item.destination));
  if (new Set(destinations).size !== destinations.length
      || destinations.some(a => destinations.some(b => a !== b && a.startsWith(b + '/'))))
    throw new Error('repository_layout_unsupported');
  const repositories = [];
  for (let index = 0; index < configured.length; index++) repositories.push(await inspectRepository(root, configured[index], index));
  const result = { version: 1, strategy: 'repository', repositories };
  if (Buffer.byteLength(JSON.stringify(result)) > LIMITS.manifestBytes) throw new Error('repository_manifest_limit');
  return result;
}

module.exports = { inspect, LIMITS };
