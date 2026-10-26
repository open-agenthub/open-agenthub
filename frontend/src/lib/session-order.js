export function orderedSessions(sessions, ids) {
  const rank = new Map((ids || []).map((id, index) => [id, index]))
  return [...sessions].sort((a, b) => (rank.get(a.id) ?? Infinity) - (rank.get(b.id) ?? Infinity))
}

export function reorderSessions(sessions, id, beforeId = null) {
  const ids = sessions.filter(s => !s.accessRole || s.accessRole === 'Owner').map(s => s.id)
  if (!ids.includes(id) || id === beforeId) return ids
  const rest = ids.filter(value => value !== id)
  const index = beforeId === null ? rest.length : rest.indexOf(beforeId)
  rest.splice(index < 0 ? rest.length : index, 0, id)
  return rest
}
