'use strict';

// Failed turns do not run Stop, but the pinned CLI records task_complete.error in its rollout.
// Read only a bounded tail and cache unchanged files: a long conversation must not be re-read
// twice a second just because somebody has Workspace open.
const TAIL_BYTES = 64 * 1024;
let cached = null;
function failedTurnEnded({ file, after, fs }) {
  if (!file) return false;
  try {
    const stat = fs.statSync(file);
    if (!cached || cached.file !== file || cached.size !== stat.size || cached.mtime !== stat.mtimeMs) {
      const length = Math.min(stat.size, TAIL_BYTES);
      const bytes = Buffer.alloc(length);
      const fd = fs.openSync(file, 'r');
      let count;
      try { count = fs.readSync(fd, bytes, 0, length, stat.size - length); }
      finally { fs.closeSync(fd); }
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
      cached = { file, size: stat.size, mtime: stat.mtimeMs, endedAt };
    }
    // A new prompt hook arrives before the new transcript lines; an old failure must not
    // suppress the next turn's working state while those writes catch up.
    return Number.isFinite(cached.endedAt) && cached.endedAt >= after;
  } catch { return false; }
}
module.exports = { failedTurnEnded, TAIL_BYTES };
