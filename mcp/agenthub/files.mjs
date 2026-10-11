import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { join, basename, win32 } from 'node:path';
import { homedir } from 'node:os';

export async function readSessionFile(client, { sessionId, fileId }, root = join(homedir(), '.agenthub', 'files')) {
  if (!/^[A-Za-z0-9_-]{1,128}$/.test(sessionId) || !/^[a-f0-9]{32}$/.test(fileId))
    throw new Error('agenthub_invalid_json');
  const file = (await client.listFiles(sessionId)).find(file => file.id === fileId);
  if (!file) throw Object.assign(new Error('file_not_found'), { code: 'file_not_found' });
  const bytes = await client.fileContent(sessionId, fileId);
  if (bytes.length !== file.size) throw Object.assign(new Error('file_size_mismatch'), { code: 'file_size_mismatch' });
  const name = win32.basename(basename(file.name));
  if (!name || name === '.' || name === '..') throw new Error('agenthub_invalid_json');
  // Unique directories and exclusive creation keep repeat downloads from overwriting local work.
  const dir = join(root, sessionId, fileId);
  await mkdir(dir, { recursive: true, mode: 0o700 });
  const localPath = join(await mkdtemp(join(dir, 'download-')), name);
  await writeFile(localPath, bytes, { flag: 'wx', mode: 0o600 });
  const descriptor = { file, localPath, readable: true };
  const content = [{ type: 'text', text: JSON.stringify(descriptor) }];
  if (['image/png', 'image/jpeg', 'image/webp', 'image/gif'].includes(file.mimeType) && bytes.length <= 20 * 1024 * 1024)
    content.unshift({ type: 'image', data: Buffer.from(bytes).toString('base64'), mimeType: file.mimeType });
  else if (['text/plain', 'text/markdown'].includes(file.mimeType) && bytes.length <= 64 * 1024) {
    try { content.unshift({ type: 'text', text: new TextDecoder('utf-8', { fatal: true }).decode(bytes) }); }
    catch { /* Keep non-UTF-8 files on disk for the caller to decode. */ }
  }
  return { content, structuredContent: descriptor };
}
