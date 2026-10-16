// The self-deletion deadline as the MCP tools take it (docs/session-expiry.md).
//
// The REST field is seconds; the tool takes "90m", "12h", "3d" or "300s" because the caller is
// a language model and that is what it writes — converting here means a wrong unit cannot turn
// twelve hours into twelve minutes on the way. Same grammar and limits as the backend's
// SessionExpiry.ParseDuration and the in-pod server's copy of this file.

const MIN_SECONDS = 300;
const MAX_SECONDS = 365 * 24 * 3600;
const UNITS = { s: 1, m: 60, h: 3600, d: 86400 };

/** Seconds for a duration with a unit; throws `autodelete_invalid_duration` for anything else. */
export function parseDuration(text) {
  const match = /^\s*(\d{1,9})\s*([smhd])\s*$/i.exec(String(text ?? ''));
  if (!match) throw new Error('autodelete_invalid_duration');
  const seconds = Number(match[1]) * UNITS[match[2].toLowerCase()];
  if (seconds < MIN_SECONDS || seconds > MAX_SECONDS) throw new Error('autodelete_invalid_duration');
  return seconds;
}

/**
 * Rewrites a create body from the tool's vocabulary (`autoDeleteAfter` as text) to the REST
 * API's (`autoDeleteAfterSeconds`). `autoDeleteFrom` passes through; the backend validates it.
 */
export function withExpiry(body = {}) {
  const { autoDeleteAfter, ...rest } = body;
  if (autoDeleteAfter === undefined || autoDeleteAfter === null || autoDeleteAfter === '') return rest;
  return { ...rest, autoDeleteAfterSeconds: parseDuration(autoDeleteAfter) };
}
