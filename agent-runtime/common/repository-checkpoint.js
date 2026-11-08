'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const { execFile, spawn } = require('node:child_process');
const LIMITS = Object.freeze({ repositories: 32, manifestBytes: 65536, pathBytes: 1024,
  entries: 10000, names: 200, namesBytes: 16384, outputBytes: 1024 * 1024, networkMs: 60000 });

// Git parses helper configuration as a command, even when execFile itself uses argument arrays.
function shellWord(value) { return "'" + value.split("'").join("'\\''") + "'"; }
function gitEnvironment(credentialFile = null) {
  const env = { ...process.env };
  // Git's inherited directory/config/trace variables can redirect an otherwise explicit cwd
  // or borrow source-only auth. A fresh clone init does not inherit these source overrides.
  for (const name of Object.keys(env)) if (name.startsWith('GIT_')) delete env[name];
  Object.assign(env, { GIT_TERMINAL_PROMPT: '0', GIT_CONFIG_NOSYSTEM: '1', GIT_NO_REPLACE_OBJECTS: '1',
    GIT_CONFIG_COUNT: '0', GIT_CONFIG_GLOBAL: '/dev/null', GIT_EXTERNAL_DIFF: '', GIT_PAGER: 'cat' });
  if (credentialFile) Object.assign(env, { HOME: path.dirname(credentialFile), GIT_ASKPASS: '/bin/false',
    SSH_ASKPASS: '/bin/false', SSH_AUTH_SOCK: '', GIT_SSL_NO_VERIFY: '0' });
  return env;
}
function git(cwd, args, credentialFile = null) {
  return new Promise((resolve, reject) => execFile('git', ['--no-optional-locks',
    '-c', 'core.fsmonitor=false', '-c', 'core.hooksPath=/dev/null',
    '-c', 'protocol.file.allow=never', '-c', 'protocol.ext.allow=never',
    '-c', 'credential.helper=', '-c', 'credential.interactive=false',
    ...(credentialFile ? ['-c', `credential.helper=store --file=${shellWord(credentialFile)}`] : []), ...args], {
    cwd, timeout: LIMITS.networkMs, maxBuffer: LIMITS.outputBytes, encoding: 'utf8',
    env: gitEnvironment(credentialFile)
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
  if (/^(?:ssh|git\+ssh):/i.test(value) || (!/^[a-z][a-z0-9+.-]*:\/\//i.test(value) && /^[^/\s:]+:.+/.test(value)))
    throw new Error('repository_ssh_unsupported');
  let url; try { url = new URL(value); } catch { throw new Error('repository_remote_invalid'); }
  if (!['https:', 'http:'].includes(url.protocol)) throw new Error('repository_transport_unsupported');
  if (url.password || url.username || url.search || url.hash) throw new Error('repository_remote_invalid');
  return value;
}

async function ignoredFiles(directory) {
  return new Promise((resolve, reject) => {
    const child = spawn('git', ['--no-optional-locks', '-c', 'core.fsmonitor=false',
      'ls-files', '--others', '--ignored', '--exclude-standard', '-z'], {
      cwd: directory, stdio: ['ignore', 'pipe', 'ignore'], env: gitEnvironment()
    });
    let pending = '', count = 0, bytes = 0, failed = false;
    const retained = [];
    const timer = setTimeout(() => { failed = true; child.kill(); }, LIMITS.networkMs);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      pending += chunk;
      let end;
      while ((end = pending.indexOf('\0')) !== -1) {
        const name = pending.slice(0, end); pending = pending.slice(end + 1);
        if (!name) continue;
        count++;
        const size = Buffer.byteLength(name);
        if (retained.length < 8 && bytes + size <= LIMITS.namesBytes) { retained.push(name); bytes += size; }
      }
      // Stream counts instead of buffering node_modules. Only a malformed single path is bounded here.
      if (pending.length > 65536) { failed = true; child.kill(); }
    });
    child.on('error', () => { failed = true; });
    child.on('close', code => {
      clearTimeout(timer);
      if (failed || code !== 0 || pending) reject(new Error('repository_check_failed'));
      else resolve({ count, names: retained, omitted: count - retained.length });
    });
  });
}

async function inspectRepository(root, configured, index, credentialFile) {
  const destination = relative(configured.destination);
  const result = { index, destination, branch: null, commit: null, remoteBranch: null,
    remoteTip: null, trackedChanges: null, untracked: null, ignored: null, unpushed: null, error: null };
  try {
    const directory = path.join(root, destination);
    if (await fs.realpath(directory) !== directory || !(await fs.lstat(path.join(directory, '.git'))).isDirectory())
      throw new Error('repository_layout_unsupported');
    const top = (await git(directory, ['rev-parse', '--show-toplevel'])).trim();
    if (top !== directory) throw new Error('repository_layout_unsupported');
    try { result.branch = (await git(directory, ['symbolic-ref', '--quiet', '--short', 'HEAD'])).trim(); }
    catch { throw new Error('repository_branch_required'); }
    if (Buffer.byteLength(result.branch) > LIMITS.pathBytes) throw new Error('repository_ref_limit');
    await git(directory, ['check-ref-format', '--branch', result.branch]);
    result.commit = (await git(directory, ['rev-parse', '--verify', 'HEAD^{commit}'])).trim();
    if (!/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(result.commit)) throw new Error('repository_commit_invalid');
    // Index and worktree edits can cancel in a diff against HEAD while staged work still exists.
    const staged = names(await git(directory, ['diff', '--cached', '--name-only', '-z', '--no-ext-diff', 'HEAD', '--']));
    const unstaged = names(await git(directory, ['diff', '--name-only', '-z', '--no-ext-diff', '--']));
    const tracked = [...new Set([...staged, ...unstaged])];
    const untracked = names(await git(directory, ['ls-files', '--others', '--exclude-standard', '-z']));
    const ignored = await ignoredFiles(directory);
    result.trackedChanges = tracked.length; result.untracked = untracked.length; result.ignored = ignored;
    const flags = names(await git(directory, ['ls-files', '-v', '-z']));
    if (flags.some(value => value.startsWith('S ') || /^[a-z] /.test(value)))
      throw new Error('repository_index_flags_unsupported');
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
    let actualUrl;
    try { actualUrl = (await git(directory, ['remote', 'get-url', 'origin'])).trim(); }
    catch { throw new Error('repository_remote_required'); }
    if (actualUrl !== configuredUrl) throw new Error('repository_remote_changed');
    const upstream = (await git(directory, ['for-each-ref', '--format=%(upstream:remotename)%00%(upstream:remoteref)',
      `refs/heads/${result.branch}`])).trim().split('\0');
    if (upstream[0] !== 'origin' || !upstream[1]?.startsWith('refs/heads/')) throw new Error('repository_upstream_required');
    result.remoteBranch = upstream[1];
    if (Buffer.byteLength(result.remoteBranch) > LIMITS.pathBytes) throw new Error('repository_ref_limit');
    await git(directory, ['check-ref-format', result.remoteBranch]);
    const verification = await fs.mkdtemp(path.join('/tmp', 'repo-check-'));
    try {
      await git(verification, ['init', '--bare', '.']);
      // A separate object database avoids trusting stale local remote refs or local objects.
      await git(verification, ['fetch', '--no-tags', '--', configuredUrl, result.remoteBranch], credentialFile);
      result.remoteTip = (await git(verification, ['rev-parse', '--verify', 'FETCH_HEAD^{commit}'])).trim();
      try {
        await git(verification, ['merge-base', '--is-ancestor', result.commit, result.remoteTip]);
        result.unpushed = 0;
      } catch {
        try {
          const count = Number((await git(directory, ['rev-list', '--count', result.commit, '--not', result.remoteTip])).trim());
          result.unpushed = Number.isSafeInteger(count) && count > 0 ? count : null;
        } catch { result.unpushed = null; }
        throw new Error('repository_commit_not_pushed');
      }
    } finally { await fs.rm(verification, { recursive: true, force: true }); }
  } catch (error) {
    result.error = /^repository_[a-z_]+$/.test(error.message) ? error.message : 'repository_check_failed';
  }
  return result;
}

async function inspect(root, configured, credentials = '') {
  root = await fs.realpath(root);
  if (!Array.isArray(configured) || configured.length === 0 || configured.length > LIMITS.repositories)
    throw new Error('repository_count_invalid');
  const destinations = configured.map(item => relative(item.destination));
  if (new Set(destinations).size !== destinations.length
      || destinations.some(a => destinations.some(b => a !== b && a.startsWith(b + '/'))))
    throw new Error('repository_layout_unsupported');
  // Both secret and Git-object scratch stay on the separate tmp volume, even if ambient
  // TMPDIR points into a provider state directory included by the periodic archive.
  const scratch = await fs.mkdtemp(path.join('/tmp', 'repo-credentials-'));
  try {
    const credentialFile = path.join(scratch, 'store');
    if (typeof credentials !== 'string' || Buffer.byteLength(credentials) > 262144) throw new Error('repository_credentials_invalid');
    await fs.writeFile(credentialFile, credentials, { mode: 0o600 });
    const repositories = [];
    for (let index = 0; index < configured.length; index++) repositories.push(await inspectRepository(root, configured[index], index, credentialFile));
    const outside = [];
    let visited = 0;
    async function walk(directory) {
      if (++visited > LIMITS.entries) throw new Error('repository_entry_limit');
      for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
        const name = path.relative(root, path.join(directory, entry.name));
        if (destinations.includes(name)) continue;
        if (++visited > LIMITS.entries) throw new Error('repository_entry_limit');
        if (entry.name === '.git') throw new Error('repository_layout_unsupported');
        if (entry.isDirectory()) await walk(path.join(directory, entry.name));
        else outside.push(name);
      }
    }
    await walk(root);
    const result = { version: 1, strategy: 'repository', repositories, outside: sample(outside) };
    if (Buffer.byteLength(JSON.stringify(result)) > LIMITS.manifestBytes) throw new Error('repository_manifest_limit');
    return result;
  } finally { await fs.rm(scratch, { recursive: true, force: true }); }
}

module.exports = { inspect, LIMITS };
