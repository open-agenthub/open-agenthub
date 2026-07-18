// Small pure helpers (unit-tested) shared across components.

/** Short display name for a repo clone URL: strips scheme/host and a trailing .git. */
export function repoShortName(url) {
  if (!url) return ''
  return String(url).replace(/^[a-z]+:\/\/[^/]+\//i, '').replace(/^git@[^:]+:/i, '').replace(/\.git$/i, '')
}

/** Whether a session matches a free-text search query (title / repo / mode). */
export function sessionMatches(session, query) {
  const q = (query || '').trim().toLowerCase()
  if (!q) return true
  const fields = [session.title, session.repoUrl, session.mode, ...((session.repos || []).map(r => r.url))]
  return fields.filter(Boolean).some(v => String(v).toLowerCase().includes(q))
}

/** Uppercase initials (max 2) for an avatar, e.g. "mira.berger" / "Mira Berger" -> "MB". */
export function initials(name) {
  const parts = String(name || '').split(/[\s._@-]+/).filter(Boolean)
  if (!parts.length) return '?'
  return parts.slice(0, 2).map(p => p[0].toUpperCase()).join('')
}
