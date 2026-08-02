import fs from 'node:fs';
import path from 'node:path';

function within(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

export async function allowedSource(source, options = {}) {
  if (typeof source !== 'string' || !source || source.length > 4096 || !path.isAbsolute(source))
    throw new Error('file_source_not_allowed');
  const workspace = await fs.promises.realpath(options.workspace ?? process.env.AGENTHUB_WORKDIR ?? '/workspace');
  const managedRoot = await fs.promises.realpath(options.managedRoot ?? process.env.AGENTHUB_FILE_ROOT ?? '/workspace/.agenthub/files');
  let canonical;
  try { canonical = await fs.promises.realpath(source); }
  catch { throw new Error('file_source_not_allowed'); }
  const stat = await fs.promises.stat(canonical);
  if (!stat.isFile() || (!within(workspace, canonical) && !within(managedRoot, canonical)))
    throw new Error('file_source_not_allowed');
  return canonical;
}

export function safeDisplayName(value) {
  if (typeof value !== 'string' || !value.trim() || value.length > 255 || value.includes('\0'))
    throw new Error('invalid_file_name');
  const name = path.win32.basename(path.posix.basename(value.trim()));
  if (!name || name === '.' || name === '..' || /[\x00-\x1f]/.test(name)) throw new Error('invalid_file_name');
  return name;
}
