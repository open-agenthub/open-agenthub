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

/**
 * Pairs of secrets that would land in the same place in one session (#253): the same delivery and
 * the same name, compared exactly as the pod sees it (API_KEY and api_key are two variables).
 * Returns [{ delivery, name, secrets: [...] }], empty when nothing collides. Stored names are not
 * unique, so this is where a duplicate becomes a problem — only once one session would get both.
 */
export function secretConflicts(secrets) {
  const groups = new Map()
  for (const secret of Array.isArray(secrets) ? secrets : []) {
    if (!secret) continue
    const key = `${secret.delivery}\u0000${secret.name}`
    const group = groups.get(key) || { delivery: secret.delivery, name: secret.name, secrets: [] }
    if (!group.secrets.some(s => s.id === secret.id)) group.secrets.push(secret)
    groups.set(key, group)
  }
  return [...groups.values()].filter(group => group.secrets.length > 1)
}

/** Where a secret lands, as the agent would look for it. */
export function secretPlace(secret) {
  return secret.delivery === 'file' ? `~/.secrets/${secret.name}` : `$${secret.name}`
}

/** The stored secrets a dialog's choices would give a session: its ticked selection and its grants. */
export function chosenSecrets(stored, selectedIds, grants) {
  const ids = new Set([...(selectedIds || []), ...(grants || []).map(g => g.secretId)])
  return (Array.isArray(stored) ? stored : []).filter(secret => ids.has(secret.id))
}

/** Whether another secret in `list` lands in the same place; the pickers then show the id, the one thing telling the two rows apart. */
export function hasNamesake(list, secret) {
  return (Array.isArray(list) ? list : []).some(other => other.id !== secret.id && other.name === secret.name && other.delivery === secret.delivery)
}

/**
 * Where a stored secret is used (#254): the projects whose selection holds it, and the owner's
 * sessions that receive it, each with how — from the hub's own `effectiveSecrets`, so this shows
 * what the sessions actually get rather than a second reading of the selection rules.
 */
export function secretUsage(secretId, sessions, projects) {
  return {
    projects: (Array.isArray(projects) ? projects : []).filter(p => (p.secretIds || []).includes(secretId)),
    sessions: (Array.isArray(sessions) ? sessions : []).flatMap(session => {
      const held = (session.effectiveSecrets || []).find(s => s.id === secretId)
      return held ? [{ session, provenance: held.provenance, inheritToChildren: !!held.inheritToChildren }] : []
    })
  }
}

/** How a session got a secret, in the words the session view uses. `project` covers an explicit
 * selection too; with a project it is that project's selection. */
export function provenanceLabel(use, projects) {
  if (use.provenance === 'granted') return 'granted directly'
  if (use.provenance === 'inherited') return 'inherited from its parent'
  if (use.provenance === 'requested') return 'requested by the agent'
  const project = (projects || []).find(p => p.id === use.session.projectId)
  return project ? `via project ${project.name}` : 'selected for it'
}

export function sessionHref(id) {
  return `/s/${encodeURIComponent(id)}`
}

/**
 * The one control for what a session gets of a secret (#329). A session stores two things: the
 * selection (`secretIds`, the project's or its own) and grants (`secretGrants`). They used to have
 * a control each, ticks for the selection and three buttons for the grants, side by side in every
 * session dialog. The three levels below cover both, because the hub already treats a selected
 * secret as one that passes to child sessions: a spawn gets the parent's selection, and
 * `secrets_list` reports `inheritToChildren` for it.
 */
export const GRANT_LEVELS = [
  { value: 'none', label: 'Not allowed' },
  { value: 'session', label: 'This session' },
  { value: 'children', label: 'Also child sessions' }
]

/**
 * The level a stored state shows. `selected` is the dialogs' list of ticked ids
 * (secretSelectionFor), `grants` the session's grants.
 */
export function secretLevel(id, selected, grants) {
  const grant = (grants || []).find(g => g.secretId === id)
  if (grant?.inheritToChildren || (selected || []).includes(id)) return 'children'
  return grant ? 'session' : 'none'
}

/**
 * The selection and grants after the owner picks `level` for one secret; everything else is left
 * as it was, so a dialog nobody touched still sends nothing. `selectable` is what the selection can
 * hold here: the project's selection inside a project, every stored secret outside one.
 * - none: neither selected nor granted.
 * - session: a grant without inheritance, and out of the selection, which would pass it on.
 * - children: an existing grant inherits; otherwise the secret goes back into the selection where
 *   it can (with all of a project's secrets back in, the session follows the project again), and
 *   only a secret the selection cannot hold becomes an inheriting grant.
 */
export function withSecretLevel(id, level, { selected = [], grants = [], selectable = [] }) {
  if (secretLevel(id, selected, grants) === level) return { selected, grants }
  const grant = grants.find(g => g.secretId === id)
  const without = selected.filter(s => s !== id)
  if (level === 'none') return { selected: without, grants: grants.filter(g => g.secretId !== id) }
  if (level === 'session') {
    return {
      selected: without,
      grants: grant ? grants.map(g => g.secretId === id ? { ...g, inheritToChildren: false } : g)
        : [...grants, { secretId: id, inheritToChildren: false }]
    }
  }
  if (grant) return { selected, grants: grants.map(g => g.secretId === id ? { ...g, inheritToChildren: true } : g) }
  if (selectable.includes(id)) return { selected: selected.includes(id) ? selected : [...selected, id], grants }
  return { selected, grants: [...grants, { secretId: id, inheritToChildren: true }] }
}
