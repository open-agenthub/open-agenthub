import { sessionMatches } from './text.js'

export function groupSessions(projects, sessions, query) {
  const matchingSessions = (sessions || []).filter(session => sessionMatches(session, query))
  const ownedSessions = matchingSessions.filter(session => !session.accessRole || session.accessRole === 'Owner')
  const projectIds = new Set((projects || []).map(project => project.id))
  const groups = (projects || [])
    .slice()
    .sort((left, right) => (left.sortOrder || 0) - (right.sortOrder || 0))
    .map(project => ({
      id: project.id,
      name: project.name,
      color: project.color,
      sortOrder: project.sortOrder,
      sessions: ownedSessions.filter(session => session.projectId === project.id)
    }))
    .filter(group => !query?.trim() || group.sessions.length)

  const sharedSessions = nestShared(matchingSessions.filter(session => session.accessRole && session.accessRole !== 'Owner'))
  if (sharedSessions.length) groups.push({ id: 'shared', name: 'Shared with me', sessions: sharedSessions })

  const ungroupedSessions = ownedSessions.filter(session => !session.projectId || !projectIds.has(session.projectId))
  if (ungroupedSessions.length) groups.push({ id: 'ungrouped', name: 'Ungrouped', sessions: ungroupedSessions })

  return groups
}

const MAX_NESTING = 64

/**
 * Orders shared sessions so each child follows its parent, with `nestDepth` for the indent and
 * `parentTitle` for the marker (#410). The backend names a parent only when the recipient can
 * open it, so a parent missing here was filtered out by the search: the child is then shown at
 * the top level rather than hidden with it. The bound mirrors the backend's chain limit and stops
 * a corrupt cycle from recursing.
 */
export function nestShared(sessions) {
  const list = sessions || []
  const byId = new Map(list.map(session => [session.id, session]))
  const children = new Map()
  const roots = []
  for (const session of list) {
    const parent = session.parentSessionId && session.parentSessionId !== session.id ? byId.get(session.parentSessionId) : null
    if (parent) children.set(parent.id, [...(children.get(parent.id) || []), session])
    else roots.push(session)
  }
  const result = []
  const placed = new Set()
  const place = (session, depth, parent) => {
    if (placed.has(session.id) || depth > MAX_NESTING) return
    placed.add(session.id)
    result.push(parent ? { ...session, nestDepth: depth, parentTitle: parent.title } : { ...session, nestDepth: 0 })
    for (const child of children.get(session.id) || []) place(child, depth + 1, session)
  }
  for (const root of roots) place(root, 0, null)
  // Sessions on a cycle have no root to hang from; they still have to be listed.
  for (const session of list) place(session, 0, null)
  return result
}
