import { mkdir, readdir, readFile, stat, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { isAbsolute, join, resolve, sep } from 'node:path';
import { gunzipSync, gzipSync } from 'node:zlib';
import { readTar, writeTar } from './tar.mjs';

// The local half of taking a session off the cluster (docs/session-transfer.md). The hub already
// retargets the conversation at the local directory; what is left is writing it where the local
// Claude Code reads it, and packing it up again for the way back.

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The folder name Claude Code derives from a working directory — the hub uses the same rule. */
export const projectFolder = directory => directory.replace(/[^A-Za-z0-9]/g, '-');

/**
 * Where Claude Code keeps its state. CLAUDE_CONFIG_DIR moves it; files written to ~/.claude
 * regardless would sit where that user's CLI never looks, and the resume would find nothing.
 */
export const claudeHome = (env = process.env) => env.CLAUDE_CONFIG_DIR || join(homedir(), '.claude');

/** Same rule as the hub's ValidateLocalDirectory, so both sides derive the same folder name. */
export function localDirectory(directory) {
  const value = String(directory ?? '').trim();
  const windows = /^[A-Za-z]:[\\/]/.test(value);
  if (!value || (!isAbsolute(value) && !windows) || value.length > 1024) throw coded('directory_invalid');
  return value.length > 3 ? value.replace(/[\\/]+$/, '') : value;
}

/**
 * Writes the hub's archive below the Claude home. Every entry has to be a regular file of one
 * conversation under .claude/projects/: the names come from a download, and an entry escaping
 * that directory is how an extraction writes somewhere it was never meant to.
 *
 * A file already there with other content is refused unless `overwrite` — it may hold turns taken
 * on this machine since the last transfer, and replacing it would lose them without a trace.
 */
export async function writeConversation(archive, { home = claudeHome(), overwrite = false } = {}) {
  const entries = readTar(gunzipSync(archive));
  const root = resolve(home, 'projects');
  const planned = [];
  for (const entry of entries) {
    if (entry.type === '5') continue;
    if (entry.type !== '0' || !entry.name.startsWith('.claude/projects/')) throw coded('archive_unexpected');
    const target = resolve(home, entry.name.slice('.claude/'.length));
    if (!target.startsWith(root + sep)) throw coded('archive_unexpected');
    planned.push({ target, data: entry.data });
  }
  if (!overwrite) {
    for (const file of planned) {
      const existing = await readFile(file.target).catch(() => null);
      if (existing && !Buffer.from(file.data).equals(existing)) throw coded('local_transcript_differs');
    }
  }
  for (const file of planned) {
    await mkdir(resolve(file.target, '..'), { recursive: true, mode: 0o700 });
    // Owner-only: a transcript holds everything the conversation saw.
    await writeFile(file.target, file.data, { mode: 0o600 });
  }
  return planned.map(file => file.target);
}

/** The local conversation as the archive the hub's hand-back endpoint takes. */
export async function packConversation(directory, sessionId, { home = claudeHome() } = {}) {
  if (!UUID.test(sessionId)) throw coded('session_id_invalid');
  const folder = projectFolder(directory);
  const base = join(home, 'projects', folder);
  const transcript = join(base, `${sessionId}.jsonl`);
  const head = await stat(transcript).catch(() => null);
  if (!head?.isFile()) throw coded('local_transcript_missing');
  const entries = [await entry(`.claude/projects/${folder}/${sessionId}.jsonl`, transcript)];
  for (const relative of await walk(join(base, sessionId))) {
    entries.push(await entry(`.claude/projects/${folder}/${sessionId}/${relative}`, join(base, sessionId, ...relative.split('/'))));
  }
  return { archive: gzipSync(writeTar(entries)), files: entries.length, transcript };
}

async function entry(name, path) {
  const info = await stat(path);
  return { name, mode: 0o600, mtime: Math.floor(info.mtimeMs / 1000), data: new Uint8Array(await readFile(path)) };
}

async function walk(dir, prefix = '') {
  const items = await readdir(dir, { withFileTypes: true }).catch(() => []);
  const files = [];
  for (const item of items) {
    const relative = prefix ? `${prefix}/${item.name}` : item.name;
    if (item.isDirectory()) files.push(...await walk(join(dir, item.name), relative));
    else if (item.isFile()) files.push(relative);
  }
  return files;
}

/** The command a person runs to continue — quoted for the shell the stdio server runs under. */
export function resumeCommand(directory, resumeId, platform = process.platform) {
  return platform === 'win32'
    ? `Set-Location '${directory.replace(/'/g, "''")}'; claude --resume ${resumeId}`
    : `cd '${directory.replace(/'/g, "'\\''")}' && claude --resume ${resumeId}`;
}

function coded(code) {
  return Object.assign(new Error(code), { code });
}
