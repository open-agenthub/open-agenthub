'use strict';

// Upward sync: reports skills that were created locally inside the session
// (directories under ~/.claude/skills that the hub does NOT manage — see the
// manifest written by skills.js) back to the hub, where they are imported into
// the library (project scope when the session has one). The hub skips skills
// whose content is unchanged, so running this repeatedly is cheap.
//
// Called by the entrypoint after the downward sync (picks up dirs restored
// from the state archive) and by the session server on graceful shutdown.

const fs = require('node:fs');
const path = require('node:path');

const { MANIFEST, NAME_RE, MAX_CONTENT, MAX_FILES, safeFilePath } = require('./skills');

const MAX_SKILLS = 50;
const MAX_SCAN_DEPTH = 3;

function readManagedNames(skillsDir, fsImpl) {
  try {
    const parsed = JSON.parse(fsImpl.readFileSync(path.join(skillsDir, MANIFEST), 'utf8'));
    return Array.isArray(parsed.skills) ? parsed.skills.filter((n) => typeof n === 'string') : [];
  } catch {
    return [];
  }
}

function readTextFile(file, fsImpl) {
  let stat;
  try { stat = fsImpl.statSync(file); } catch { return null; }
  if (!stat.isFile() || stat.size > MAX_CONTENT) return null;
  const content = fsImpl.readFileSync(file);
  if (content.includes(0)) return null; // binary
  return content.toString('utf8');
}

function collectFiles(dir, fsImpl) {
  const files = [];
  const walk = (current, prefix, depth) => {
    if (depth > MAX_SCAN_DEPTH || files.length >= MAX_FILES) return;
    for (const entry of fsImpl.readdirSync(current, { withFileTypes: true })) {
      if (files.length >= MAX_FILES) return;
      const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        walk(path.join(current, entry.name), relative, depth + 1);
        continue;
      }
      if (relative === 'SKILL.md' || !safeFilePath(relative)) continue;
      const content = readTextFile(path.join(current, entry.name), fsImpl);
      if (content !== null) files.push({ path: relative, content });
    }
  };
  walk(dir, '', 1);
  return files;
}

/** Extracts the frontmatter description, so imported skills stay discoverable. */
function extractDescription(skillMd) {
  const frontmatter = /^---\n([\s\S]*?)\n---/.exec(skillMd);
  if (!frontmatter) return '';
  const line = frontmatter[1].split('\n').find((l) => l.startsWith('description:'));
  return line ? line.slice('description:'.length).trim().replace(/^['"]|['"]$/g, '') : '';
}

/** Scans for unmanaged skill directories and builds the import payload. */
function collectLocalSkills(skillsDir, deps = {}) {
  const fsImpl = deps.fs || fs;
  if (!fsImpl.existsSync(skillsDir)) return [];
  const managed = new Set(readManagedNames(skillsDir, fsImpl));
  const skills = [];
  for (const entry of fsImpl.readdirSync(skillsDir, { withFileTypes: true })) {
    if (skills.length >= MAX_SKILLS) break;
    if (!entry.isDirectory() || managed.has(entry.name)) continue;
    if (!NAME_RE.test(entry.name) || entry.name.length > 64) continue;
    const dir = path.join(skillsDir, entry.name);
    const content = readTextFile(path.join(dir, 'SKILL.md'), fsImpl);
    if (!content) continue;
    skills.push({
      name: entry.name,
      description: extractDescription(content),
      content,
      files: collectFiles(dir, fsImpl)
    });
  }
  return skills;
}

async function syncUp(env = process.env, deps = {}) {
  const fetchImpl = deps.fetch || fetch;
  const home = env.HOME;
  const callback = env.AGENTHUB_CALLBACK_URL;
  const token = env.AGENTHUB_CALLBACK_TOKEN;
  if (!home || !callback || !token) return { sent: 0 };

  const skills = collectLocalSkills(path.join(home, '.claude', 'skills'), deps);
  if (!skills.length) return { sent: 0 };

  const response = await fetchImpl(`${callback}/skills/import`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
    body: JSON.stringify({ skills }),
    signal: AbortSignal.timeout(10_000)
  });
  if (!response.ok) throw new Error(`import failed with ${response.status}`);
  const result = await response.json().catch(() => ({}));
  return { sent: skills.length, result };
}

module.exports = { collectLocalSkills, extractDescription, syncUp };

if (require.main === module) {
  syncUp().then(({ sent, result }) => {
    if (!sent) {
      console.log('[skills] No local skills to report.');
      return;
    }
    const summary = result
      ? ` (created: ${(result.created || []).length}, updated: ${(result.updated || []).length}, ` +
        `unchanged: ${(result.unchanged || []).length}, skipped: ${(result.skipped || []).length})`
      : '';
    console.log(`[skills] Reported ${sent} local skill(s) to the hub${summary}.`);
  }).catch((error) => {
    console.error(`[skills] Upward sync failed: ${error.message}`);
    process.exitCode = 1;
  });
}
