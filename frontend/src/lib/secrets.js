// The secret selection of a session as the dialogs edit it (docs/session-secrets.md).
//
// On the wire a session's `secretIds` is null ("follow the project", which is nothing outside a
// project), an explicit list (narrowed), or ['*'] in an edit to go back to null — the same
// conventions as the git token selection, with the opposite default: outside a project nothing
// is ticked until the owner ticks it.

export const DELIVERIES = [
  { value: 'env', label: 'Environment variable' },
  { value: 'file', label: 'File in ~/.secrets' }
]

export function deliveryLabel(delivery) {
  return delivery === 'file' ? 'file' : 'env'
}

/** The secrets a session in `project` can be given: the project's selection, or every stored one outside a project. */
export function secretOptions(secrets, project) {
  const list = Array.isArray(secrets) ? secrets : []
  if (!project) return list
  const ids = Array.isArray(project.secretIds) ? project.secretIds : []
  return list.filter(secret => ids.includes(secret.id))
}

/** The ids a stored selection ticks: null ticks the whole project, or nothing outside one. */
export function secretSelectionFor(current, options, inProject) {
  const ids = options.map(secret => secret.id)
  if (Array.isArray(current)) return ids.filter(id => current.includes(id))
  return inProject ? ids : []
}

function ticked(selected, options) {
  return options.map(secret => secret.id).filter(id => selected.includes(id))
}

function followsProject(selected, options, inProject) {
  return inProject ? options.length === 0 || ticked(selected, options).length === options.length
    : ticked(selected, options).length === 0
}

/** What a create sends: null to follow the project (or for nothing outside one), else the ticked ids. */
export function secretIdsPayload(selected, options, inProject) {
  return followsProject(selected, options, inProject) ? null : ticked(selected, options)
}

/**
 * What an edit or duplicate sends: nothing when the selection is what the session already has,
 * ['*'] to go back to following the project (null means "unchanged" in an edit), else the ids.
 */
export function secretIdsChange(selected, options, inProject, current) {
  // A project that selects nothing offers no choice, so there is nothing to change either.
  if (inProject && options.length === 0) return {}
  const follow = followsProject(selected, options, inProject)
  if (!Array.isArray(current)) return follow ? {} : { secretIds: ticked(selected, options) }
  if (follow) return inProject ? { secretIds: ['*'] } : (current.length ? { secretIds: [] } : {})
  const chosen = ticked(selected, options)
  const kept = secretSelectionFor(current, options, inProject)
  const same = chosen.length === kept.length && chosen.every(id => kept.includes(id))
  return same ? {} : { secretIds: chosen }
}
