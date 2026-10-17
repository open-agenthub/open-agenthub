// The server's own sentence from an { error } body, for messages a person has to act on
// (e.g. "This would leave no admin"); anything else falls back to the raw "status body"
// that api.js puts into the Error.
export function errorText(e) {
  const raw = String(e?.message ?? e)
  const match = /^\d{3} (.*)$/s.exec(raw)
  try { return JSON.parse(match[1]).error || raw } catch { return raw }
}
