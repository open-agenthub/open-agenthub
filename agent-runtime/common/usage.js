'use strict';

// Token usage read from the files each agent CLI writes for itself (docs/usage-tracking.md).
// A driver's readUsage({ env, home, fs }) returns the session's cumulative totals in the hub's
// buckets, or null when the CLI has written nothing yet:
//   inputTokens          uncached input (cache reads/writes are their own buckets)
//   outputTokens         output INCLUDING reasoning, as every provider bills it
//   cacheReadTokens, cacheCreationTokens, reasoningTokens (a part of outputTokens)
//   turns                prompts the agent took on
//   costUsd              what the CLI itself computed, or null when it computes nothing
// Totals, not deltas: the hub keeps the last report and adds only the difference, so a report
// that is lost or sent twice neither drops nor double-counts usage.

const path = require('node:path');

function emptyTotals() {
  return { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
    reasoningTokens: 0, turns: 0, costUsd: null };
}

function count(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? Math.round(number) : 0;
}

function money(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : 0;
}

function listFiles(root, fileSystem, accept) {
  const found = [];
  const walk = dir => {
    let entries;
    try { entries = fileSystem.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (accept(entry.name, full)) found.push(full);
    }
  };
  walk(root);
  return found.sort();
}

function readFrom(fileSystem, file, offset, size) {
  if (typeof fileSystem.openSync === 'function' && typeof fileSystem.readSync === 'function' &&
      typeof fileSystem.closeSync === 'function') {
    const buffer = Buffer.alloc(size - offset);
    const fd = fileSystem.openSync(file, 'r');
    try {
      let read = 0;
      while (read < buffer.length) {
        const n = fileSystem.readSync(fd, buffer, read, buffer.length - read, offset + read);
        if (n <= 0) break;
        read += n;
      }
      return buffer.subarray(0, read);
    } finally {
      fileSystem.closeSync(fd);
    }
  }
  return Buffer.from(fileSystem.readFileSync(file)).subarray(offset, size);
}

/**
 * Sums usage over append-only JSONL files, reading each file only from where the last call
 * stopped: a rollout of a long session is many megabytes, and the persistence tick asks every
 * 30 seconds. extract(record, file) returns { key, usage } for a usage record (key de-duplicates
 * a record the CLI writes again, e.g. on resume), { turn: true } for a new turn, or null.
 * A file that got shorter was rewritten, so the reader starts over rather than miss records.
 */
function createJsonlUsageReader(extract) {
  let files = new Map();
  let seen = new Set();
  let totals = emptyTotals();
  let reportsCost = false;

  function reset() {
    files = new Map(); seen = new Set(); totals = emptyTotals(); reportsCost = false;
  }

  function apply(record, file) {
    const found = extract(record, file);
    if (!found) return;
    if (found.turn) { totals.turns += 1; return; }
    if (!found.usage || seen.has(found.key)) return;
    seen.add(found.key);
    const usage = found.usage;
    totals.inputTokens += count(usage.inputTokens);
    totals.outputTokens += count(usage.outputTokens);
    totals.cacheReadTokens += count(usage.cacheReadTokens);
    totals.cacheCreationTokens += count(usage.cacheCreationTokens);
    totals.reasoningTokens += count(usage.reasoningTokens);
    if (usage.costUsd !== undefined && usage.costUsd !== null) {
      reportsCost = true;
      totals.costUsd = (totals.costUsd || 0) + money(usage.costUsd);
    }
  }

  return function read(paths, fileSystem) {
    for (const file of paths) {
      let size;
      try { size = fileSystem.statSync(file).size; } catch { continue; }
      const state = files.get(file) || { offset: 0, rest: '' };
      if (size < state.offset) { reset(); return read(paths, fileSystem); }
      if (size === state.offset) { files.set(file, state); continue; }
      let chunk;
      try { chunk = readFrom(fileSystem, file, state.offset, size); } catch { continue; }
      state.offset += chunk.length;
      const text = state.rest + chunk.toString('utf8');
      const lines = text.split('\n');
      state.rest = lines.pop();
      files.set(file, state);
      for (const line of lines) {
        if (!line.trim()) continue;
        let record;
        try { record = JSON.parse(line); } catch { continue; }
        apply(record, file);
      }
    }
    if (!files.size) return null;
    return { ...totals, costUsd: reportsCost ? totals.costUsd : null };
  };
}

// Codex 0.160 writes one `token_usage_record` per model response (keyed by response_id) and an
// `event_msg` `task_started` per turn into $CODEX_HOME/sessions/**/rollout-*.jsonl. Its
// input_tokens INCLUDE the cached part, unlike Claude's; reasoning is part of output_tokens.
// Codex computes no price, so costUsd stays null.
function codexUsageRecord(record, file) {
  if (!record || typeof record !== 'object') return null;
  const payload = record.payload || {};
  if (record.type === 'event_msg' && payload.type === 'task_started') return { turn: true };
  if (record.type !== 'token_usage_record' || !payload.usage) return null;
  const usage = payload.usage;
  const cached = count(usage.cached_input_tokens);
  return {
    key: payload.response_id
      ? 'r:' + (payload.thread_id || '') + ':' + payload.response_id
      : 'l:' + file + ':' + record.ordinal,
    usage: {
      inputTokens: Math.max(0, count(usage.input_tokens) - cached),
      cacheReadTokens: cached,
      cacheCreationTokens: count(usage.cache_write_input_tokens),
      outputTokens: count(usage.output_tokens),
      reasoningTokens: count(usage.reasoning_output_tokens)
    }
  };
}

// OpenClaw 2026.7 keeps each conversation in $OPENCLAW_STATE_DIR/agents/<agent>/sessions/<id>.jsonl;
// every assistant entry carries `usage` with its own price-table cost (the .trajectory.jsonl next
// to it repeats the same responses and is skipped). output already includes reasoningTokens.
function openClawUsageRecord(record, file) {
  if (!record || record.type !== 'message' || !record.message) return null;
  const message = record.message;
  if (message.role === 'user') return { turn: true };
  if (message.role !== 'assistant' || !message.usage) return null;
  const usage = message.usage;
  return {
    key: file + ':' + record.id,
    usage: {
      inputTokens: usage.input,
      outputTokens: usage.output,
      cacheReadTokens: usage.cacheRead,
      cacheCreationTokens: usage.cacheWrite,
      reasoningTokens: usage.reasoningTokens,
      costUsd: usage.cost && typeof usage.cost === 'object' ? usage.cost.total : usage.cost
    }
  };
}

// OpenCode 1.18 keeps everything in one SQLite database. An aggregate over its message table is
// cheap enough to run on every tick, so there is no incremental state. Its tokens.output EXCLUDES
// reasoning (tokens.reasoning is separate), so the two are added for the hub's output bucket.
// Turns are the user messages of top-level sessions: a subagent's "user" message is a prompt the
// model wrote, not one the person sent.
const OPENCODE_USAGE_SQL = `
  SELECT
    COALESCE(SUM(CASE WHEN json_extract(m.data, '$.role') = 'assistant' THEN json_extract(m.data, '$.tokens.input') END), 0) AS input,
    COALESCE(SUM(CASE WHEN json_extract(m.data, '$.role') = 'assistant' THEN json_extract(m.data, '$.tokens.output') END), 0) AS output,
    COALESCE(SUM(CASE WHEN json_extract(m.data, '$.role') = 'assistant' THEN json_extract(m.data, '$.tokens.reasoning') END), 0) AS reasoning,
    COALESCE(SUM(CASE WHEN json_extract(m.data, '$.role') = 'assistant' THEN json_extract(m.data, '$.tokens.cache.read') END), 0) AS cache_read,
    COALESCE(SUM(CASE WHEN json_extract(m.data, '$.role') = 'assistant' THEN json_extract(m.data, '$.tokens.cache.write') END), 0) AS cache_write,
    COALESCE(SUM(CASE WHEN json_extract(m.data, '$.role') = 'assistant' THEN json_extract(m.data, '$.cost') END), 0) AS cost,
    COUNT(CASE WHEN json_extract(m.data, '$.role') = 'assistant' THEN 1 END) AS responses,
    COUNT(CASE WHEN json_extract(m.data, '$.role') = 'user' AND s.parent_id IS NULL THEN 1 END) AS turns
  FROM message m LEFT JOIN session s ON s.id = m.session_id`;

function readOpenCodeUsage(dbFile, { fs: fileSystem, openDatabase } = {}) {
  try { if (!fileSystem.statSync(dbFile).isFile()) return null; } catch { return null; }
  let db;
  try {
    db = (openDatabase || defaultOpenDatabase)(dbFile);
    const row = db.prepare(OPENCODE_USAGE_SQL).get();
    if (!row || (!row.responses && !row.turns)) return null;
    return {
      inputTokens: count(row.input),
      outputTokens: count(row.output) + count(row.reasoning),
      cacheReadTokens: count(row.cache_read),
      cacheCreationTokens: count(row.cache_write),
      reasoningTokens: count(row.reasoning),
      turns: count(row.turns),
      costUsd: money(row.cost)
    };
  } catch {
    // A database mid-migration, locked or from another schema is skipped for this tick; usage
    // is cumulative, so the next readable tick catches up.
    return null;
  } finally {
    try { if (db) db.close(); } catch {}
  }
}

function defaultOpenDatabase(file) {
  // Loaded on first use: node:sqlite prints an experimental warning on Node 22, and only the
  // OpenCode runtime needs it.
  const { DatabaseSync } = require('node:sqlite');
  return new DatabaseSync(file, { readOnly: true });
}

// One incremental reader per state directory, so its offsets never mix two homes' files.
function readerPerRoot(extract) {
  const readers = new Map();
  return root => {
    if (!readers.has(root)) readers.set(root, createJsonlUsageReader(extract));
    return readers.get(root);
  };
}

module.exports = {
  readerPerRoot,
  emptyTotals, listFiles, createJsonlUsageReader,
  codexUsageRecord, openClawUsageRecord, readOpenCodeUsage, OPENCODE_USAGE_SQL
};
