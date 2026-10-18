'use strict';

// Installs the session's git credential store and the per-host commit author that goes with each
// stored token (docs/git-credentials.md, "Several tokens per host"). Used twice: by the entrypoint
// for what the gitcreds secret holds at start, and by the session agent when the owner switches the
// token of a running session — so both paths produce the same files.
//
// The author is per host, not global: a session can push to two hosts with two tokens, and a group
// token on one must not stamp its bot identity on commits to the other. Git has no conditional
// [user] section, only conditional includes, so every host gets a small file with its [user] and
// one file with the includeIf lines points at them. `hasconfig:remote.*.url` (git 2.36+) is the
// condition because it follows the repository's remote, which is what the token is chosen by;
// `gitdir:` would follow a path the agent picks.

const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

// The whole store is a few lines per host; the cap only keeps a bad request from writing a large
// file into the home directory.
const MAX_CREDENTIALS_BYTES = 64 * 1024;
const MAX_AUTHORS = 64;
// The same rules the backend enforces when a token is stored (GitPatStore, ManualGitCredentials):
// checked again here because the values are written into git config syntax, where a quote or a
// newline would end the value and start an arbitrary section.
const HOST = /^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?(:[0-9]{1,5})?$/;
const UNSAFE_AUTHOR = /[\x00-\x1f\x7f"\\<>]/;
const STORE_LINE = /^https:\/\/[^\s:@/]+:[^\s@/]+@[A-Za-z0-9.-]+(:[0-9]{1,5})?$/;

function authorsDir(home) { return path.join(home, '.config', 'agenthub', 'git-authors'); }
function authorsInclude(home) { return path.join(home, '.config', 'agenthub', 'git-authors.gitconfig'); }

function validAuthor(author) {
  if (!author || typeof author !== 'object') return false;
  const { host, name, email } = author;
  if (typeof host !== 'string' || !HOST.test(host)) return false;
  if (name == null && email == null) return false;
  for (const value of [name, email]) {
    if (value == null) continue;
    if (typeof value !== 'string' || !value.trim() || value.length > 254 || UNSAFE_AUTHOR.test(value)) return false;
  }
  return email == null || (/@/.test(email) && !/\s/.test(email));
}

function validCredentials(text) {
  if (typeof text !== 'string' || Buffer.byteLength(text) > MAX_CREDENTIALS_BYTES) return false;
  return text.split('\n').filter(Boolean).every(line => STORE_LINE.test(line));
}

/** Throws on anything that is not {credentials: string|null, authors: [...]}. */
function parsePayload(body) {
  let parsed;
  try { parsed = JSON.parse(Buffer.isBuffer(body) ? body.toString('utf8') : String(body)); } catch { parsed = null; }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('invalid_git_credentials');
  const credentials = parsed.credentials == null ? '' : parsed.credentials;
  const authors = parsed.authors == null ? [] : parsed.authors;
  if (!validCredentials(credentials)) throw new Error('invalid_git_credentials');
  if (!Array.isArray(authors) || authors.length > MAX_AUTHORS || !authors.every(validAuthor)) {
    throw new Error('invalid_git_authors');
  }
  return { credentials, authors };
}

function writePrivate(file, content, fsImpl) {
  fsImpl.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
  const temp = `${file}.${process.pid}.tmp`;
  fsImpl.writeFileSync(temp, content, { mode: 0o600 });
  fsImpl.renameSync(temp, file);
}

/** The includeIf patterns that mean "a remote on this host": https, ssh:// and scp-style. */
function remotePatterns(host) {
  const patterns = [`https://${host}/**`];
  // An ssh remote never carries the https port, so a host with a port only matches over https.
  if (!host.includes(':')) patterns.push(`ssh://*@${host}/**`, `*@${host}:*`, `*@${host}:*/**`);
  return patterns;
}

function quote(value) { return `"${value}"`; }

/**
 * Writes the per-host author files and the include file that selects them. Always rewritten as a
 * whole, so a switch to a token without an author drops the previous token's identity instead of
 * leaving it in force.
 */
function writeAuthors(home, authors, fsImpl = fs) {
  const dir = authorsDir(home);
  fsImpl.rmSync(dir, { recursive: true, force: true });
  const sections = [];
  authors.forEach((author, index) => {
    const file = path.join(dir, `${index}.gitconfig`);
    const lines = ['[user]'];
    if (author.name) lines.push(`\tname = ${quote(author.name.trim())}`);
    if (author.email) lines.push(`\temail = ${quote(author.email.trim())}`);
    writePrivate(file, lines.join('\n') + '\n', fsImpl);
    for (const pattern of remotePatterns(author.host)) {
      sections.push(`[includeIf ${quote(`hasconfig:remote.*.url:${pattern}`)}]`, `\tpath = ${quote(file)}`);
    }
  });
  writePrivate(authorsInclude(home), sections.length ? sections.join('\n') + '\n' : '', fsImpl);
}

/**
 * Points the global config at the include file once. Added after user.name/user.email are set from
 * the owner's stored identity, so for a matching remote the token's author wins — git takes the
 * last value it reads.
 */
function ensureInclude(home, run = execFileSync) {
  const file = authorsInclude(home);
  let current = '';
  try {
    current = run('git', ['config', '--global', '--get-all', 'include.path'], { encoding: 'utf8', env: { ...process.env, HOME: home } });
  } catch { current = ''; }
  if (current.split('\n').map(line => line.trim()).includes(file)) return;
  run('git', ['config', '--global', '--add', 'include.path', file], { env: { ...process.env, HOME: home } });
}

/**
 * Installs a payload: the store when `credentials` is given (an empty one removes the file — the
 * session now has no token), the authors, the include, and gh/glab derived from the store again.
 */
function install({ home, credentials, authors }, { fsImpl = fs, run = execFileSync, setupCliAuth } = {}) {
  if (typeof credentials === 'string') {
    const store = path.join(home, '.git-credentials');
    if (credentials) {
      writePrivate(store, credentials.endsWith('\n') ? credentials : credentials + '\n', fsImpl);
      run('git', ['config', '--global', 'credential.helper', 'store'], { env: { ...process.env, HOME: home } });
    } else {
      fsImpl.rmSync(store, { force: true });
    }
    // The CLI configs are derived from the store and rewritten only when it has lines, so the old
    // token would otherwise stay in them after a switch to "no token for this host".
    fsImpl.rmSync(path.join(home, '.config', 'gh', 'hosts.yml'), { force: true });
    fsImpl.rmSync(path.join(home, '.config', 'glab-cli', 'config.yml'), { force: true });
    if (setupCliAuth) run('bash', [setupCliAuth, store], { env: { ...process.env, HOME: home } });
  }
  writeAuthors(home, authors || [], fsImpl);
  ensureInclude(home, run);
}

module.exports = { parsePayload, install, writeAuthors, ensureInclude, remotePatterns, authorsInclude, MAX_CREDENTIALS_BYTES };

// Entrypoint use: `node git-identity.js <authors.json>` — only the authors, because the entrypoint
// copies the store itself (the clone init container needs it before node is involved).
if (require.main === module) {
  const home = process.env.HOME || '/home/agent';
  let authors = [];
  const file = process.argv[2];
  if (file && fs.existsSync(file)) {
    try {
      authors = parsePayload(JSON.stringify({ authors: JSON.parse(fs.readFileSync(file, 'utf8')) })).authors;
    } catch (error) {
      console.error(`[git-identity] ignoring ${file}: ${error.message}`);
    }
  }
  install({ home, authors });
}
