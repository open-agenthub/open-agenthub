import fs from 'node:fs';
import path from 'node:path';

// Skill refresh can replace the plugin instance while its previous snapshot is still pending.
// One process-wide queue keeps both instances from appending the same native message.
let recordQueue = Promise.resolve();

// The CLI owns SQLite. Use its plugin client rather than opening the live database from the
// transport process, and retain source.path while omitting the redundant image data URI.
export function createTranscriptRecorder({ client, env = process.env, fsImpl = fs } = {}) {
  async function record(sessionID, submitted) {
    if (!/^ses_[A-Za-z0-9]+$/.test(sessionID || '')) return;
    const session = await client.session.get({ path: { id: sessionID } });
    if (!session?.data || session.data.parentID) return;
    const root = path.join(env.HOME || '/home/agent', '.opencode', 'agenthub-transcripts');
    fsImpl.mkdirSync(root, { recursive: true, mode: 0o700 });
    const file = path.join(root, sessionID + '.jsonl');
    const index = file + '.ids.json';
    let ids = [];
    try { ids = JSON.parse(fsImpl.readFileSync(index, 'utf8')); } catch {}
    const recent = new Set(Array.isArray(ids) ? ids.slice(-200) : []);
    // Recovery rows can predate the side index; they prove presence, not recency.
    const seen = new Set(recent);
    const remember = id => { recent.delete(id); recent.add(id); };
    const persistIndex = () => {
      fsImpl.writeFileSync(index + '.part', JSON.stringify([...recent].slice(-200)), { mode: 0o600 });
      fsImpl.renameSync(index + '.part', index);
    };
    // A crash after append but before the index rename must not replay that last message.
    // The normalized record is bounded; only its tail needs reading on startup or restart.
    let fd;
    try {
      fd = fsImpl.openSync(file, 'r');
      const size = fsImpl.fstatSync(fd).size;
      const tail = Buffer.alloc(Math.min(size, 512 * 1024));
      fsImpl.readSync(fd, tail, 0, tail.length, size - tail.length);
      for (const line of tail.toString('utf8').split('\n')) {
        try { seen.add(JSON.parse(line).uuid); } catch {}
      }
    } catch {} finally { if (fd !== undefined) fsImpl.closeSync(fd); }
    const messages = !submitted || seen.size === 0
      ? [...((await client.session.messages({ path: { id: sessionID }, query: { limit: 100 } }))?.data || [])] : [];
    if (submitted) messages.push(submitted);
    for (const { info, parts = [] } of messages) {
      if (!info?.id || !['user', 'assistant'].includes(info.role)) continue;
      // Streaming answers are mutable; append them only once complete so byte cursors stay valid.
      if (info.role === 'assistant' && !info.time?.completed) continue;
      if (seen.has(info.id)) { remember(info.id); continue; }
      let remaining = 50_000;
      let imageCount = 0;
      const content = parts.filter(part => !part.synthetic).slice(0, 100).flatMap(part => {
        if (part.type === 'text' && typeof part.text === 'string') {
          const text = part.text.slice(0, remaining);
          remaining -= text.length;
          return text ? [{ type: 'text', text }] : [];
        }
        if (info.role === 'user' && part.type === 'file' && part.mime?.startsWith('image/') &&
            typeof part.source?.path === 'string' && part.source.path.length <= 4096 && imageCount < 5) {
          imageCount++;
          return [{ type: 'file', source: { path: part.source.path },
            name: typeof part.filename === 'string' ? part.filename.slice(0, 255) : undefined }];
        }
        return [];
      });
      if (!content.length) continue;
      fsImpl.appendFileSync(file, JSON.stringify({ type: info.role, uuid: info.id,
        timestamp: new Date(info.time?.created || Date.now()).toISOString(), message: { role: info.role, content } }) + '\n', { mode: 0o600 });
      seen.add(info.id);
      remember(info.id);
      // Commit each append: after a crash only one bounded record can be ahead of the index,
      // even when the first SDK snapshot contains more bytes than the recovery tail.
      persistIndex();
    }
    persistIndex();
  }
  return (sessionID, submitted) => {
    recordQueue = recordQueue.catch(() => {}).then(() => record(sessionID, submitted));
    return recordQueue;
  };
}
