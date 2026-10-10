'use strict';

// Reads a provider CLI's own "you are at your usage limit" notice out of what it prints
// (docs/account-limits.md, "Everyone else: the session agent reads the output"). Each driver
// exports the sentences its pinned CLI version holds as `limitPatterns`; this applies them to
// the PTY stream, the stdout lines of a chat pipe, and stderr, and reports the first hit per
// agent start. One hit per start on purpose: a TUI keeps its notice on screen and redraws it,
// and the hub does not need to hear the same limit once per repaint.

// CSI, OSC and the two-byte escapes a TUI emits around its text. The 7-bit forms only; a
// TUI on a UTF-8 terminal never sends the 8-bit C1 forms, and stripping those would eat
// characters out of multibyte text.
const ANSI = /\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]/g;
// How much stripped text is kept for matching. A notice is a sentence; the window only has to
// be long enough that a sentence split across two chunks still lands inside it.
const TAIL_CHARS = 4096;
// The matching line, as the hub stores it next to the mark.
const MAX_DETAIL_CHARS = 300;

function stripAnsi(text) {
  return String(text).replace(ANSI, '');
}

function compile(patterns) {
  const list = [];
  for (const entry of Array.isArray(patterns) ? patterns : []) {
    if (!entry || !(entry.pattern instanceof RegExp)) continue;
    // A global regex keeps lastIndex between calls and would skip a match; use a copy without it.
    const flags = entry.pattern.flags.replace('g', '');
    list.push({
      pattern: new RegExp(entry.pattern.source, flags),
      resetsAt: typeof entry.resetsAt === 'function' ? entry.resetsAt : null
    });
  }
  return list;
}

// The line the match sits on, out of a buffer whose line breaks may be \n or the \r of a TUI.
function lineAround(text, index) {
  const start = Math.max(text.lastIndexOf('\n', index), text.lastIndexOf('\r', index)) + 1;
  let end = text.length;
  for (const terminator of ['\n', '\r']) {
    const at = text.indexOf(terminator, index);
    if (at !== -1 && at < end) end = at;
  }
  const line = text.slice(start, end).trim();
  return line.length > MAX_DETAIL_CHARS ? line.slice(0, MAX_DETAIL_CHARS) : line;
}

/** A reset time as the hub takes it: an ISO string, or null when the value is unreadable. */
function toIso(value, now = Date.now) {
  if (value === null || value === undefined || value === '') return null;
  if (typeof value === 'number' && Number.isFinite(value)) {
    // Unix seconds (a provider's `resets_at`) or milliseconds; anything else is not a time.
    const ms = value > 1e12 ? value : value > 1e9 ? value * 1000 : null;
    return ms === null ? null : new Date(ms).toISOString();
  }
  if (typeof value === 'string') {
    const parsed = Date.parse(value);
    return Number.isNaN(parsed) ? null : new Date(parsed).toISOString();
  }
  return null;
}

function secondsFromNow(seconds, now = Date.now) {
  return Number.isFinite(seconds) && seconds > 0 ? new Date(now() + seconds * 1000).toISOString() : null;
}

/**
 * Looks for a limit notice in `text` (already free of control sequences). Returns the hit —
 * `{ detail, resetsAt }` — or null.
 */
function matchLimit(patterns, text, now = Date.now) {
  for (const entry of patterns) {
    const match = entry.pattern.exec(text);
    if (!match) continue;
    let resetsAt = null;
    if (entry.resetsAt) {
      try { resetsAt = toIso(entry.resetsAt(match, text, { now, secondsFromNow: s => secondsFromNow(s, now) }), now); } catch {}
    }
    return { detail: lineAround(text, match.index), resetsAt };
  }
  return null;
}

/**
 * Builds a detector for one driver. `onHit({ detail, resetsAt })` is called at most once
 * between two `reset()` calls, from whichever feed saw the notice first.
 */
function createLimitDetector(options = {}) {
  const patterns = compile(options.patterns);
  const now = options.now || Date.now;
  const onHit = typeof options.onHit === 'function' ? options.onHit : () => {};
  let tail = '';
  let fired = false;

  function hit(result) {
    if (fired || !result) return;
    fired = true;
    onHit(result);
  }

  function feedText(chunk) {
    if (fired || patterns.length === 0) return;
    tail += stripAnsi(chunk);
    if (tail.length > TAIL_CHARS) tail = tail.slice(-TAIL_CHARS);
    hit(matchLimit(patterns, tail, now));
  }

  // One stream-json line. A `result` or `error` event carries the text the CLI would have
  // printed; the assistant's own messages are not searched, so a model quoting the sentence
  // does not count as the CLI printing it.
  function feedChatLine(line) {
    if (fired || patterns.length === 0) return;
    let event;
    try { event = JSON.parse(line); } catch { return; }
    if (!event || typeof event !== 'object') return;
    if (event.type !== 'result' && event.type !== 'error') return;
    const texts = [];
    for (const field of ['result', 'error', 'message']) {
      if (typeof event[field] === 'string') texts.push(event[field]);
      else if (event[field] && typeof event[field] === 'object' && typeof event[field].message === 'string') texts.push(event[field].message);
    }
    if (Array.isArray(event.errors)) for (const entry of event.errors) if (typeof entry === 'string') texts.push(entry);
    if (texts.length === 0) return;
    hit(matchLimit(patterns, texts.join('\n'), now));
  }

  function reset() {
    tail = '';
    fired = false;
  }

  return {
    feedTerminal: feedText,
    feedStderr: feedText,
    feedChatLine,
    reset,
    get fired() { return fired; },
    get armed() { return patterns.length > 0; }
  };
}

module.exports = { createLimitDetector, matchLimit, stripAnsi, toIso, secondsFromNow, lineAround, TAIL_CHARS };
