import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

/**
 * Pre-accepts Claude Code's workspace trust dialog for the session's working directory.
 *
 * Verified against the pinned CLI (2.1.283): an interactive `claude` in a directory it has not
 * been told to trust renders "Quick safety check: Is this a project you trust?" and runs nothing
 * at all until somebody presses Enter. Print mode skips the dialog on its own, which is why
 * autonomous sessions never hit it — but it means an interactive session started with an initial
 * prompt would sit on the dialog rather than working, and an API-created session handed to a
 * person an hour later would have made no progress.
 *
 * Nothing is being consented to on the user's behalf that they did not already choose: the
 * directory is the workspace of a session their own account asked for, holding the repositories
 * they named.
 */
export function trustWorkspace(config, dir) {
  const projects = config && typeof config.projects === 'object' && !Array.isArray(config.projects)
    ? config.projects : {};
  const project = projects[dir] && typeof projects[dir] === 'object' ? projects[dir] : {};
  return {
    ...config,
    hasCompletedOnboarding: true,
    projects: { ...projects, [dir]: { ...project, hasTrustDialogAccepted: true } }
  };
}

export function writeWorkspaceTrust(configPath, dir) {
  let config = {};
  // A merge rather than a fresh write: a custom image may ship its own ~/.claude.json, and
  // overwriting it would drop settings the image deliberately set. Read straight away and
  // treat a failure as "nothing to merge" — an existence check first would describe a
  // different file than the one that is then read.
  try { config = JSON.parse(fs.readFileSync(configPath, 'utf8')); }
  catch { config = {}; }
  if (!config || typeof config !== 'object' || Array.isArray(config)) config = {};
  fs.writeFileSync(configPath, `${JSON.stringify(trustWorkspace(config, dir))}\n`, { mode: 0o600 });
  return configPath;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  writeWorkspaceTrust(process.argv[2], process.argv[3]);
