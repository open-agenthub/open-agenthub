'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createSecretMask } = require('../common/secret-mask');

const PAGE_MESSAGES = 100;
const MAX_PARTS = 100;
const MAX_TEXT = 50_000;
const OMITTED = '… [content not shown]';

// OpenCode owns this database. A short read transaction gives each exported message one
// consistent set of parts without holding a lock while the transport writes its journal.
function readPage(db, sessionID, cursor) {
  db.exec('BEGIN');
  try {
    const session = db.prepare('SELECT parent_id FROM session WHERE id = ?').get(sessionID);
    if (!session || session.parent_id) return [];
    const messages = db.prepare(`SELECT id, time_created,
      json_extract(data, '$.role') AS role,
      json_extract(data, '$.time.completed') AS completed,
      substr(json_extract(data, '$.error.name'), 1, 255) AS error,
      EXISTS(SELECT 1 FROM message successor WHERE successor.session_id = message.session_id
        AND successor.id > message.id AND json_extract(successor.data, '$.role') = 'assistant') AS has_reply
      FROM message WHERE session_id = ? AND id > ? ORDER BY id LIMIT ?`).all(sessionID, cursor, PAGE_MESSAGES);
    const parts = db.prepare(`SELECT id,
      json_extract(data, '$.type') AS type, json_extract(data, '$.synthetic') AS synthetic,
      CASE WHEN length(json_extract(data, '$.text')) > 50000 THEN '[Large content not shown]' ELSE json_extract(data, '$.text') END AS text,
      substr(json_extract(data, '$.mime'), 1, 255) AS mime,
      substr(json_extract(data, '$.source.path'), 1, 4097) AS source,
      substr(json_extract(data, '$.filename'), 1, 256) AS filename,
      substr(json_extract(data, '$.tool'), 1, 255) AS tool,
      substr(json_extract(data, '$.callID'), 1, 255) AS callID,
      json_extract(data, '$.state.status') AS status,
      CASE WHEN length(json_extract(data, '$.state.input')) > 2000 THEN '[Large content not shown]' ELSE json_extract(data, '$.state.input') END AS input,
      CASE WHEN length(json_extract(data, '$.state.output')) > 2000 THEN '[Large content not shown]' ELSE json_extract(data, '$.state.output') END AS output,
      CASE WHEN length(json_extract(data, '$.state.error')) > 2000 THEN '[Large content not shown]' ELSE json_extract(data, '$.state.error') END AS error
      FROM part WHERE message_id = ? ORDER BY id LIMIT ?`);
    const page = [];
    let characters = 0;
    for (const message of messages) {
      // Completion freezes streaming text/tool state. Do not allow a later user prompt to
      // overtake an unfinished answer and permanently strand it behind the durable cursor.
      if (message.role === 'assistant' && !message.completed && !message.error) break;
      // Native user parts are persisted after their message row. The assistant is created
      // only after those writes, so waiting for it prevents freezing a half-written prompt.
      if (message.role === 'user' && !message.has_reply) break;
      const nativeParts = parts.all(message.id, MAX_PARTS + 1);
      if (!nativeParts.length && message.role === 'user') break;
      page.push({ ...message, parts: nativeParts });
      characters += nativeParts.reduce((total, part) => total + Object.values(part).reduce((sum, value) =>
        sum + (typeof value === 'string' ? value.length : 0), 0), 0);
      // Message and part limits alone allow hundreds of megabytes of large text parts.
      // Stop after this message so one poll holds at most one oversized bounded message.
      if (characters >= 256_000) break;
    }
    return page;
  } finally { db.exec('ROLLBACK'); }
}

function normalize(message, mask = value => value) {
  let remaining = MAX_TEXT;
  let images = 0;
  const content = [];
  const clip = (value, limit) => {
    if (typeof value !== 'string') return '';
    value = mask(value);
    return value.length > limit ? value.slice(0, limit) + OMITTED : value;
  };
  const bounded = (value, limit) => {
    const text = clip(value, Math.min(limit, remaining));
    remaining = Math.max(0, remaining - Math.min(typeof value === 'string' ? value.length : 0, limit));
    return text;
  };
  for (const part of message.parts.slice(0, MAX_PARTS)) {
    if (part.synthetic) continue;
    if (part.type === 'text' && part.text) {
      const text = bounded(part.text, remaining);
      if (text) content.push({ type: 'text', text });
    } else if (part.type === 'tool') {
      let input;
      const rawInput = bounded(part.input, 2000);
      try { input = JSON.parse(rawInput); } catch { input = rawInput; }
      content.push({ type: 'tool_use', id: part.callID, name: part.tool, input });
      if (['completed', 'error'].includes(part.status)) {
        content.push({ type: 'tool_result', tool_use_id: part.callID,
          content: bounded(part.status === 'error' ? part.error : part.output, 2000),
          is_error: part.status === 'error' });
      } else {
        content.push({ type: 'tool_result', tool_use_id: part.callID, content: '[Tool interrupted before completion]' });
      }
    } else if (message.role === 'user' && part.type === 'file' && part.mime?.startsWith('image/') &&
        part.source && part.source.length <= 4096 && images < 5) {
      images++;
      content.push({ type: 'file', source: { path: part.source }, name: clip(part.filename, 255) });
    }
  }
  if (message.parts.length > MAX_PARTS) content.push({ type: 'text', text: '[Additional message parts not shown]' });
  if (message.error) content.push({ type: 'text', text: '[Turn ended: ' + message.error + ']' });
  return { type: message.role, uuid: message.id, opencodeCursor: message.id,
    timestamp: new Date(message.time_created).toISOString(), message: { role: message.role, content } };
}

// Each bounded journal row carries its own cursor: a crash cannot leave an index ahead of
// the append. Truncate an interrupted last append before resuming from the last whole row.
function journalCursor(file, fsImpl) {
  let fd;
  try {
    fd = fsImpl.openSync(file, 'r+');
    const size = fsImpl.fstatSync(fd).size;
    const tail = Buffer.alloc(Math.min(size, 1024 * 1024));
    fsImpl.readSync(fd, tail, 0, tail.length, size - tail.length);
    const end = tail.lastIndexOf(10);
    if (end < 0) { if (size === tail.length) fsImpl.ftruncateSync(fd, 0); return ''; }
    if (end < tail.length - 1) fsImpl.ftruncateSync(fd, size - tail.length + end + 1);
    const start = tail.lastIndexOf(10, end - 1) + 1;
    const row = JSON.parse(tail.subarray(start, end).toString('utf8'));
    return /^msg_[A-Za-z0-9]+$/.test(row.opencodeCursor || '') ? row.opencodeCursor : '';
  } catch (error) {
    if (error.code === 'ENOENT') return '';
    throw error;
  } finally { if (fd !== undefined) fsImpl.closeSync(fd); }
}

function exportTranscript({ home, sessionID, fsImpl = fs, openDatabase, env = process.env, onUnavailable = () => {} }) {
  if (!/^ses_[A-Za-z0-9]+$/.test(sessionID || '')) return null;
  const root = path.join(home, '.opencode');
  const file = path.join(root, 'agenthub-native-transcripts', sessionID + '.jsonl');
  let db;
  try {
    db = (openDatabase || (file => new (require('node:sqlite').DatabaseSync)(file, { readOnly: true, timeout: 0 })))(path.join(root, 'opencode.db'));
    const cursor = journalCursor(file, fsImpl);
    const page = readPage(db, sessionID, cursor);
    fsImpl.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
    const secrets = createSecretMask({ env, fs: fsImpl });
    secrets.refresh();
    for (const message of page) fsImpl.appendFileSync(file, JSON.stringify(normalize(message, secrets.mask)) + '\n', { mode: 0o600 });
    return fsImpl.existsSync(file) ? file : null;
  } catch (error) {
    onUnavailable(error);
    // A migration, busy database or missing restored store must keep the last readable page
    // available. The next poll retries from the journal's committed cursor.
    return null;
  } finally { try { db?.close(); } catch {} }
}

module.exports = { exportTranscript, readPage, normalize, journalCursor, PAGE_MESSAGES };
