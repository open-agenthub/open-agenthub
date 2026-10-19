'use strict';

// Failed turns do not run Stop, but the pinned CLI records task_complete.error in its rollout.
// Read only a bounded tail and cache unchanged files: a long conversation must not be re-read
// twice a second just because somebody has Workspace open.
const TAIL_BYTES = 64 * 1024;
let cached = null;
function failedTurnEnded({ file, after, fs }) {
  if (!file) return false;
  let fd;
  try {
    fd = fs.openSync(file, 'r');
    // Inspect the opened descriptor so replacement between a path stat and read cannot
    // combine metadata from one rollout with bytes from another.
    const stat = fs.fstatSync(fd);
    if (!cached || cached.file !== file || cached.size !== stat.size || cached.mtime !== stat.mtimeMs || cached.ino !== stat.ino || cached.dev !== stat.dev) {
      const length = Math.min(stat.size, TAIL_BYTES);
      const bytes = Buffer.alloc(length);
      const count = fs.readSync(fd, bytes, 0, length, stat.size - length);
      let text = bytes.subarray(0, count).toString('utf8');
      if (stat.size > length) text = text.slice(text.indexOf('\n') + 1);
      let endedAt = null;
      for (const line of text.split('\n').reverse()) {
        let event;
        try { event = JSON.parse(line); } catch { continue; }
        if (event?.type !== 'event_msg') continue;
        const kind = event.payload?.type;
        if (!['task_started', 'task_complete', 'turn_aborted'].includes(kind)) continue;
        if (kind === 'turn_aborted' || (kind === 'task_complete' && event.payload.error)) endedAt = Date.parse(event.timestamp);
        break;
      }
      cached = { file, size: stat.size, mtime: stat.mtimeMs, ino: stat.ino, dev: stat.dev, endedAt };
    }
    // A new prompt hook arrives before the new transcript lines; an old failure must not
    // suppress the next turn's working state while those writes catch up.
    return Number.isFinite(cached.endedAt) && cached.endedAt >= after;
  } catch { return false; }
  finally { if (fd !== undefined) try { fs.closeSync(fd); } catch {} }
}
module.exports = { failedTurnEnded, TAIL_BYTES };
