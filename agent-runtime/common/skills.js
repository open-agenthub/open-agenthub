'use strict';

// Materializes the hub-managed skill library into ~/.claude/skills: once at pod start (from the
// entrypoint) and then periodically while the session runs (from the session agent), so a skill
// saved by another session appears without a restart. See docs/skill-library-sync.md for which
// agent CLI reads the directory when.
//
// A manifest file tracks which skill directories the hub manages, so removed or unshared skills
// disappear while skills the user created inside the session are never touched. It also keeps a
// hash of what was written per skill: a managed directory whose content no longer matches was
// edited locally, and is kept rather than overwritten by the next pull.

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const MANIFEST = '.agenthub-managed.json';
const NAME_RE = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;
const MAX_NAME = 64;
const MAX_CONTENT = 200_000;
const MAX_FILES = 20;
// Mirrors the backend rules: relative, shallow, safe segments, no dot-segments.
const FILE_SEGMENT_RE = /^[A-Za-z0-9_-][A-Za-z0-9._-]*$/;
// Next to the skills directory, not inside it: every agent CLI scans the skills directory for
// */SKILL.md, and a half-written copy there would show up as a second skill of the same name.
// The same filesystem, so the final step is a rename.
const STAGING = '.agenthub-skills-staging';
const LOCK = '.agenthub-skills-sync.lock';
const LOCK_STALE_MS = 120_000;
const MAX_HASH_DEPTH = 4;
const MAX_HASH_FILES = 200;

function safeFilePath(candidate) {
  if (typeof candidate !== 'string' || !candidate || candidate.length > 200) return null;
  const value = candidate.replace(/\\/g, '/');
  if (value.toUpperCase() === 'SKILL.MD') return null;
  const segments = value.split('/');
  if (segments.length > 3) return null;
  for (const segment of segments) {
    if (!FILE_SEGMENT_RE.test(segment)) return null;
  }
  return value;
}

function readManifest(skillsDir, fsImpl = fs) {
  let parsed;
  try {
    parsed = JSON.parse(fsImpl.readFileSync(path.join(skillsDir, MANIFEST), 'utf8'));
  } catch {
    return { skills: [], hashes: {}, generation: 0, etag: null, exists: false };
  }
  const skills = Array.isArray(parsed.skills) ? parsed.skills.filter((n) => typeof n === 'string') : [];
  const hashes = parsed.hashes && typeof parsed.hashes === 'object' ? parsed.hashes : {};
  return {
    skills,
    hashes,
    generation: Number.isSafeInteger(parsed.generation) ? parsed.generation : 0,
    etag: typeof parsed.etag === 'string' ? parsed.etag : null,
    exists: true
  };
}

/** One hash over SKILL.md and every file, so the library's copy and the local one compare. */
function hashEntries(entries) {
  const hash = crypto.createHash('sha256');
  for (const [file, content] of [...entries].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
    hash.update(file).update('\0').update(content).update('\0');
  }
  return hash.digest('hex');
}

function hashPayload(skill) {
  return hashEntries([['SKILL.md', skill.content], ...skill.files.map((f) => [f.path, f.content])]);
}

/** Hash of a skill directory as it is on disk now; null when it is missing or empty. */
function hashDirectory(dir, fsImpl = fs) {
  const entries = [];
  const walk = (current, prefix, depth) => {
    if (depth > MAX_HASH_DEPTH || entries.length > MAX_HASH_FILES) return;
    for (const entry of fsImpl.readdirSync(current, { withFileTypes: true })) {
      const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) walk(full, relative, depth + 1);
      else if (entry.isFile()) entries.push([relative, fsImpl.readFileSync(full)]);
    }
  };
  try {
    walk(dir, '', 1);
  } catch {
    return null;
  }
  return entries.length ? hashEntries(entries) : null;
}

function validSkills(payload, result) {
  const skills = (payload && Array.isArray(payload.skills)) ? payload.skills : [];
  const valid = new Map();
  for (const skill of skills) {
    const name = skill && typeof skill.name === 'string' ? skill.name : '';
    const content = skill && typeof skill.content === 'string' ? skill.content : '';
    if (!NAME_RE.test(name) || name.length > MAX_NAME || !content || content.length > MAX_CONTENT) {
      result.skipped.push(name || '(invalid)');
      continue;
    }
    const files = [];
    for (const file of Array.isArray(skill.files) ? skill.files.slice(0, MAX_FILES) : []) {
      const safePath = file ? safeFilePath(file.path) : null;
      if (!safePath || typeof file.content !== 'string' || file.content.length > MAX_CONTENT) continue;
      files.push({ path: safePath, content: file.content });
    }
    if (!valid.has(name)) valid.set(name, { content, files });
  }
  return valid;
}

/**
 * Builds the skill in the staging directory and swaps it in with renames, so an agent CLI that
 * rescans while this runs sees the old directory or the new one, never a half-written one.
 */
function writeSkill(skillsDir, name, skill, fsImpl) {
  const staging = path.join(path.dirname(skillsDir), STAGING);
  const suffix = `${process.pid}-${crypto.randomBytes(4).toString('hex')}`;
  const fresh = path.join(staging, `${name}.new-${suffix}`);
  const old = path.join(staging, `${name}.old-${suffix}`);
  fsImpl.mkdirSync(fresh, { recursive: true });
  fsImpl.writeFileSync(path.join(fresh, 'SKILL.md'), skill.content);
  for (const file of skill.files) {
    const target = path.join(fresh, file.path);
    fsImpl.mkdirSync(path.dirname(target), { recursive: true });
    fsImpl.writeFileSync(target, file.content);
  }
  const dir = path.join(skillsDir, name);
  const existed = fsImpl.existsSync(dir);
  if (existed) fsImpl.renameSync(dir, old);
  fsImpl.renameSync(fresh, dir);
  if (existed) fsImpl.rmSync(old, { recursive: true, force: true });
}

function writeManifest(skillsDir, manifest, fsImpl) {
  const staging = path.join(path.dirname(skillsDir), STAGING);
  fsImpl.mkdirSync(staging, { recursive: true });
  const temp = path.join(staging, `${MANIFEST}.${process.pid}`);
  fsImpl.writeFileSync(temp, JSON.stringify(manifest, null, 2));
  fsImpl.renameSync(temp, path.join(skillsDir, MANIFEST));
}

/**
 * Brings skillsDir in line with the library payload.
 *
 * result.kept lists managed skills that were edited locally: neither overwritten nor removed,
 * because a pull that silently replaced an edit the agent made a minute ago would lose work. They
 * stay managed (so the upward sync does not import them as new skills) until the library's
 * version matches the local one again, which is what upload_skill of the edit produces.
 */
function materializeSkills(payload, skillsDir, deps = {}) {
  const fsImpl = deps.fs || fs;
  const before = readManifest(skillsDir, fsImpl);
  const result = { written: [], removed: [], skipped: [], kept: [], unchanged: [] };

  fsImpl.mkdirSync(skillsDir, { recursive: true });
  const valid = validSkills(payload, result);
  const managed = [];
  const hashes = {};

  // Locally edited means: we recorded what we wrote, and the directory no longer matches it.
  // A manifest from before hashes were recorded has none, and is treated as unedited.
  const editedLocally = (name, localHash) =>
    typeof before.hashes[name] === 'string' && localHash !== null && localHash !== before.hashes[name];

  for (const name of before.skills) {
    if (valid.has(name) || !NAME_RE.test(name)) continue;
    const dir = path.join(skillsDir, name);
    if (editedLocally(name, hashDirectory(dir, fsImpl))) {
      managed.push(name);
      hashes[name] = before.hashes[name];
      result.kept.push(name);
      continue;
    }
    fsImpl.rmSync(dir, { recursive: true, force: true });
    result.removed.push(name);
  }

  for (const [name, skill] of valid) {
    const dir = path.join(skillsDir, name);
    const wasManaged = before.skills.includes(name);
    // Never clobber a skill directory the user created inside the session.
    if (!wasManaged && fsImpl.existsSync(dir)) {
      result.skipped.push(name);
      continue;
    }
    const wanted = hashPayload(skill);
    const localHash = wasManaged ? hashDirectory(dir, fsImpl) : null;
    managed.push(name);
    if (localHash === wanted) {
      hashes[name] = wanted;
      result.unchanged.push(name);
      continue;
    }
    if (wasManaged && editedLocally(name, localHash)) {
      hashes[name] = before.hashes[name];
      result.kept.push(name);
      continue;
    }
    writeSkill(skillsDir, name, skill, fsImpl);
    hashes[name] = wanted;
    result.written.push(name);
  }

  const changed = result.written.length > 0 || result.removed.length > 0;
  const manifest = {
    skills: managed.sort(),
    hashes,
    // Bumped only when a directory changed, so a reader (OpenCode's refresher) can tell a pull
    // that found something from one that did not.
    generation: before.generation + (changed ? 1 : 0),
    etag: deps.etag !== undefined ? deps.etag : before.etag
  };
  const previous = { skills: before.skills, hashes: before.hashes, generation: before.generation, etag: before.etag };
  if (changed || !before.exists || JSON.stringify(manifest) !== JSON.stringify(previous)) {
    writeManifest(skillsDir, manifest, fsImpl);
  }
  result.generation = manifest.generation;
  return result;
}

function skillsDirFor(env) {
  return env.HOME ? path.join(env.HOME, '.claude', 'skills') : null;
}

/**
 * One pull from the hub. Two pulls never run at once (the entrypoint's and the session agent's
 * first tick can overlap), because both would stage and rename the same directories.
 */
async function pullSkills(env = process.env, deps = {}) {
  const fsImpl = deps.fs || fs;
  const fetchImpl = deps.fetch || fetch;
  const skillsDir = deps.skillsDir || skillsDirFor(env);
  const callback = env.AGENTHUB_CALLBACK_URL;
  const token = env.AGENTHUB_CALLBACK_TOKEN;
  if (!skillsDir || !callback || !token) return { skippedReason: 'not configured' };

  const lock = path.join(path.dirname(skillsDir), LOCK);
  fsImpl.mkdirSync(path.dirname(skillsDir), { recursive: true });
  try {
    fsImpl.mkdirSync(lock);
  } catch {
    let stale = false;
    try { stale = Date.now() - fsImpl.statSync(lock).mtimeMs > LOCK_STALE_MS; } catch { stale = true; }
    if (!stale) return { skippedReason: 'another pull is running' };
    fsImpl.rmSync(lock, { recursive: true, force: true });
    try { fsImpl.mkdirSync(lock); } catch { return { skippedReason: 'another pull is running' }; }
  }
  try {
    // Whatever is in staging was left by a pull that died mid-write (pod killed, archived and
    // restored with the state); under the lock nothing else can be using it.
    fsImpl.rmSync(path.join(path.dirname(skillsDir), STAGING), { recursive: true, force: true });
    const { etag } = readManifest(skillsDir, fsImpl);
    const headers = { 'X-Agent-Token': token };
    // Only when the directory exists: a deleted skills dir with a stored etag would otherwise be
    // answered 304 forever and never come back.
    if (etag && fsImpl.existsSync(skillsDir)) headers['If-None-Match'] = etag;
    const response = await fetchImpl(`${callback}/skills`, {
      headers,
      signal: AbortSignal.timeout(deps.timeoutMs || 15_000)
    });
    if (response.status === 304) return { notModified: true };
    if (!response.ok) throw new Error(`skills request failed with ${response.status}`);
    const payload = await response.json();
    return materializeSkills(payload, skillsDir, { fs: fsImpl, etag: response.headers.get('etag') });
  } finally {
    fsImpl.rmSync(lock, { recursive: true, force: true });
  }
}

/**
 * Points ~/.agents/skills at ~/.claude/skills. Codex, Cursor and OpenClaw read ~/.agents/skills
 * and not ~/.claude/skills (verified per CLI, see docs/skill-library-sync.md); a link instead of
 * a second copy keeps one directory to sync, one manifest and one place for the upward sync to
 * find skills the agent created — whichever of the two paths it wrote them through.
 */
function linkAgentsSkills(home, deps = {}) {
  const fsImpl = deps.fs || fs;
  const link = path.join(home, '.agents', 'skills');
  const target = path.join('..', '.claude', 'skills');
  fsImpl.mkdirSync(path.join(home, '.claude', 'skills'), { recursive: true });
  let stat = null;
  try { stat = fsImpl.lstatSync(link); } catch { /* absent */ }
  if (stat && stat.isSymbolicLink()) {
    if (fsImpl.readlinkSync(link) === target) return 'linked';
    fsImpl.unlinkSync(link);
  } else if (stat) {
    // Somebody's own directory (restored state, a custom image). Replacing it would delete their
    // skills; leaving it means this agent sees those instead of the library.
    return 'kept-existing';
  }
  fsImpl.mkdirSync(path.dirname(link), { recursive: true });
  fsImpl.symlinkSync(target, link);
  return 'linked';
}

function summary(result) {
  if (result.notModified) return 'unchanged';
  if (result.skippedReason) return `skipped (${result.skippedReason})`;
  return `${result.written.length} written, ${result.removed.length} removed, ` +
    `${result.skipped.length} skipped, ${result.kept.length} kept with local edits` +
    (result.kept.length ? ` (${result.kept.join(', ')})` : '');
}

module.exports = {
  materializeSkills, pullSkills, linkAgentsSkills, summary, hashDirectory, readManifest,
  MANIFEST, NAME_RE, MAX_CONTENT, MAX_FILES, safeFilePath
};

if (require.main === module) {
  const args = process.argv.slice(2);
  const home = process.env.HOME;
  if (!home) {
    console.error('[skills] HOME is not set.');
    process.exit(1);
  }
  if (args.includes('--link-agents')) {
    try {
      console.log(linkAgentsSkills(home) === 'linked'
        ? '[skills] ~/.agents/skills points at ~/.claude/skills.'
        : '[skills] WARN: ~/.agents/skills is a directory of its own; library skills are not visible there.');
    } catch (error) {
      console.error(`[skills] Could not link ~/.agents/skills: ${error.message}`);
      process.exitCode = 1;
    }
  }
  if (args.includes('--pull')) {
    pullSkills().then((result) => {
      console.log(`[skills] ${summary(result)}.`);
    }).catch((error) => {
      console.error(`[skills] Pull failed: ${error.message}`);
      process.exitCode = 1;
    });
  } else if (!args.includes('--link-agents')) {
    let raw = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => { raw += chunk; });
    process.stdin.on('end', () => {
      let payload;
      try {
        payload = JSON.parse(raw || '{}');
      } catch (error) {
        console.error(`[skills] Invalid payload: ${error.message}`);
        process.exit(1);
      }
      console.log(`[skills] ${summary(materializeSkills(payload, skillsDirFor(process.env)))}.`);
    });
  }
}
