'use strict';

// A transcript record (one JSONL line) that does not fit into one page of
// GET /agenthub/transcript (#452). Codex writes an image tool result — view_image, a browser
// screenshot — as one line holding the image as a data url, and a compaction that carries
// images repeats them all in one line; either reaches tens of megabytes. The reader shows
// none of those bytes: Codex tool output is clipped to 2,000 characters and image parts are
// not rendered from the transcript at all. So the record is kept, with its longest strings cut
// until the whole line is small, rather than the conversation stopping in front of it.

// What one slimmed record may come to. Well under the 8 MiB page, so masking a cut string
// cannot push it over; large enough that any message a person typed survives uncut.
const SLIM_RECORD_BYTES = 1024 * 1024;
// What is kept of a string that is cut: enough to recognise it, nothing to read through.
const KEPT_CHARS = 2048;

// Cuts the longest strings first, so a person's message next to an image stays whole and only
// the image data goes. Returns null for a line that is not a JSON object or array: a broken
// record has nothing to recover, and the cursor still moves past it.
function slimRecord(line, limit = SLIM_RECORD_BYTES) {
  let record;
  try { record = JSON.parse(line); } catch { return null; }
  if (record === null || typeof record !== 'object') return null;
  const strings = [];
  const walk = (holder, key) => {
    const value = holder[key];
    if (typeof value === 'string') { if (value.length > KEPT_CHARS) strings.push({ holder, key, length: value.length }); return; }
    if (value && typeof value === 'object') for (const inner of Object.keys(value)) walk(value, inner);
  };
  walk({ record }, 'record');
  strings.sort((a, b) => b.length - a.length);
  let size = Buffer.byteLength(line);
  for (const string of strings) {
    if (size <= limit) break;
    const cut = string.holder[string.key].slice(0, KEPT_CHARS) + `… [${string.length - KEPT_CHARS} characters not shown]`;
    size -= string.length - cut.length;
    string.holder[string.key] = cut;
  }
  const slim = JSON.stringify(record);
  return Buffer.byteLength(slim) <= limit ? slim : null;
}

module.exports = { slimRecord, SLIM_RECORD_BYTES };
