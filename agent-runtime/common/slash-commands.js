'use strict';

// The slash commands the Workspace composer offers while a person types "/" (#169,
// docs/slash-commands.md). None of the five CLIs reports its command list on the PTY it runs
// on, so the list is the runtime's record of the pinned CLI's built-ins plus what the CLI
// would load from disk itself: custom command files and skills. Executing stays the CLI's
// job — the composer types the command into the terminal like any other message.

const path = require('node:path');

const MAX_COMMANDS = 400;
const MAX_ENTRIES_PER_DIR = 500;
// Frontmatter sits at the top; a description further down is not one a CLI shows either.
const MAX_HEAD_BYTES = 8 * 1024;
const MAX_CONFIG_BYTES = 256 * 1024;
const MAX_DEPTH = 3;
const MAX_TEXT = 200;
// What a CLI accepts after its "/" and what is safe to show and type back: no spaces, quotes
// or control characters a file name could otherwise smuggle into the composer.
const NAME = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,79}$/;

function clip(value) {
  if (typeof value !== 'string') return undefined;
  const text = value.replace(/[\x00-\x1f\x7f]+/g, ' ').trim();
  if (!text) return undefined;
  return text.length > MAX_TEXT ? text.slice(0, MAX_TEXT - 1) + '…' : text;
}

function readHead(fileSystem, file, max = MAX_HEAD_BYTES) {
  let fd;
  try {
    fd = fileSystem.openSync(file, 'r');
    const buffer = Buffer.alloc(max);
    const read = fileSystem.readSync(fd, buffer, 0, max, 0);
    return buffer.subarray(0, read).toString('utf8');
  } catch {
    return null;
  } finally {
    if (fd !== undefined) try { fileSystem.closeSync(fd); } catch {}
  }
}

// Flat "key: value" frontmatter, which is all the command and skill files use for the fields
// read here. A full YAML parser would be a dependency for three scalar keys.
function frontmatter(text) {
  const fields = {};
  if (typeof text !== 'string') return fields;
  const match = /^---\r?\n([\s\S]*?)\r?\n---/.exec(text);
  if (!match) return fields;
  for (const line of match[1].split(/\r?\n/)) {
    const field = /^([A-Za-z][A-Za-z0-9_-]*):\s*(.*)$/.exec(line);
    if (!field) continue;
    let value = field[2].trim();
    if (/^(['"]).*\1$/.test(value)) value = value.slice(1, -1);
    fields[field[1].toLowerCase()] = value;
  }
  return fields;
}

function listDir(fileSystem, dir) {
  try {
    return fileSystem.readdirSync(dir, { withFileTypes: true }).slice(0, MAX_ENTRIES_PER_DIR);
  } catch {
    return [];
  }
}

// Markdown command files: <dir>/name.md is /name. Claude Code also loads subdirectories as
// /sub:name; `namespaced` says whether this CLI does.
function commandFiles(fileSystem, dir, { namespaced = false } = {}, prefix = '', depth = 0, out = []) {
  for (const entry of listDir(fileSystem, dir)) {
    if (entry.isSymbolicLink()) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (namespaced && depth < MAX_DEPTH) commandFiles(fileSystem, full, { namespaced }, prefix + entry.name + ':', depth + 1, out);
      continue;
    }
    if (!entry.isFile() || !entry.name.endsWith('.md')) continue;
    const meta = frontmatter(readHead(fileSystem, full));
    out.push({
      name: prefix + entry.name.slice(0, -3),
      description: clip(meta.description),
      argumentHint: clip(meta['argument-hint'])
    });
  }
  return out;
}

// Skills: <dir>/<skill>/SKILL.md. A skill marked `user-invocable: false` is one the CLI hides
// from its own "/" menu, so it is left out here as well.
function skillFiles(fileSystem, dir) {
  const out = [];
  for (const entry of listDir(fileSystem, dir)) {
    if (!entry.isDirectory() && !entry.isSymbolicLink()) continue;
    const head = readHead(fileSystem, path.join(dir, entry.name, 'SKILL.md'));
    if (head === null) continue;
    const meta = frontmatter(head);
    if (String(meta['user-invocable']).toLowerCase() === 'false') continue;
    out.push({
      name: meta.name || entry.name,
      description: clip(meta.description),
      argumentHint: clip(meta['argument-hint'])
    });
  }
  return out;
}

// OpenCode keeps custom commands in its JSON config as { command: { name: { description } } }.
function configCommands(fileSystem, file) {
  const text = readHead(fileSystem, file, MAX_CONFIG_BYTES);
  if (!text) return [];
  let config;
  try { config = JSON.parse(text); } catch { return []; }
  const commands = config && typeof config.command === 'object' && !Array.isArray(config.command) ? config.command : {};
  return Object.entries(commands).map(([name, value]) => ({
    name,
    description: clip(value && value.description)
  }));
}

function fromSource(fileSystem, source) {
  if (source.kind === 'commands') return commandFiles(fileSystem, source.dir, source);
  if (source.kind === 'skills') return skillFiles(fileSystem, source.dir);
  if (source.kind === 'config') return configCommands(fileSystem, source.file);
  return [];
}

// First one wins, in the order built-ins, then sources as the catalog lists them: a CLI runs its
// built-in when a custom command reuses the name, so offering the custom description would lie.
function listSlashCommands(catalog, { env = {}, home = '', cwd = '', fs: fileSystem }) {
  const seen = new Set();
  const commands = [];
  let truncated = false;
  const add = (entry, source) => {
    if (!entry || !NAME.test(entry.name) || seen.has(entry.name)) return;
    if (commands.length >= MAX_COMMANDS) { truncated = true; return; }
    seen.add(entry.name);
    const command = { name: entry.name, source };
    if (entry.description) command.description = entry.description;
    if (entry.argumentHint) command.argumentHint = entry.argumentHint;
    if (entry.effect) command.effect = entry.effect;
    commands.push(command);
  };
  for (const entry of catalog.builtins || []) add(entry, 'builtin');
  const sources = typeof catalog.sources === 'function' ? catalog.sources({ env, home, cwd }) : [];
  for (const source of sources) {
    for (const entry of fromSource(fileSystem, source)) add(entry, source.kind === 'skills' ? 'skill' : 'custom');
  }
  return { cli: catalog.cli, commands, truncated };
}

module.exports = { listSlashCommands, frontmatter, MAX_COMMANDS };
