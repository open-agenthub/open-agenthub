'use strict';

// Materializes the hub-managed skill library into ~/.claude/skills.
// Reads the backend's JSON payload ({ skills: [{ name, content }] }) from stdin
// (piped by the entrypoint from GET $AGENTHUB_CALLBACK_URL/skills).
//
// A manifest file tracks which skill directories the hub manages, so removed
// or unshared skills disappear on the next start while skills the user created
// inside the session (restored from the state archive) are never touched.

const fs = require('node:fs');
const path = require('node:path');

const MANIFEST = '.agenthub-managed.json';
const NAME_RE = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;
const MAX_NAME = 64;
const MAX_CONTENT = 200_000;

function readManifest(skillsDir, fsImpl) {
  try {
    const parsed = JSON.parse(fsImpl.readFileSync(path.join(skillsDir, MANIFEST), 'utf8'));
    return Array.isArray(parsed.skills) ? parsed.skills.filter((n) => typeof n === 'string') : [];
  } catch {
    return [];
  }
}

function materializeSkills(payload, skillsDir, deps = {}) {
  const fsImpl = deps.fs || fs;
  const skills = (payload && Array.isArray(payload.skills)) ? payload.skills : [];
  const managedBefore = readManifest(skillsDir, fsImpl);
  const result = { written: [], removed: [], skipped: [] };

  fsImpl.mkdirSync(skillsDir, { recursive: true });

  const valid = new Map();
  for (const skill of skills) {
    const name = skill && typeof skill.name === 'string' ? skill.name : '';
    const content = skill && typeof skill.content === 'string' ? skill.content : '';
    if (!NAME_RE.test(name) || name.length > MAX_NAME || !content || content.length > MAX_CONTENT) {
      result.skipped.push(name || '(invalid)');
      continue;
    }
    if (!valid.has(name)) valid.set(name, content);
  }

  // Drop directories the hub managed last time that are gone from the library now.
  for (const name of managedBefore) {
    if (valid.has(name) || !NAME_RE.test(name)) continue;
    fsImpl.rmSync(path.join(skillsDir, name), { recursive: true, force: true });
    result.removed.push(name);
  }

  for (const [name, content] of valid) {
    const dir = path.join(skillsDir, name);
    // Never clobber a skill directory the user created inside the session.
    if (!managedBefore.includes(name) && fsImpl.existsSync(dir)) {
      result.skipped.push(name);
      continue;
    }
    fsImpl.mkdirSync(dir, { recursive: true });
    fsImpl.writeFileSync(path.join(dir, 'SKILL.md'), content);
    result.written.push(name);
  }

  fsImpl.writeFileSync(
    path.join(skillsDir, MANIFEST),
    JSON.stringify({ skills: result.written }, null, 2));
  return result;
}

module.exports = { materializeSkills, MANIFEST };

if (require.main === module) {
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
    const home = process.env.HOME;
    if (!home) {
      console.error('[skills] HOME is not set.');
      process.exit(1);
    }
    const result = materializeSkills(payload, path.join(home, '.claude', 'skills'));
    console.log(
      `[skills] ${result.written.length} written, ${result.removed.length} removed, ` +
      `${result.skipped.length} skipped.`);
  });
}
