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

  const sharedSessions = matchingSessions.filter(session => session.accessRole && session.accessRole !== 'Owner')
  if (sharedSessions.length) groups.push({ id: 'shared', name: 'Shared with me', sessions: sharedSessions })

  const ungroupedSessions = ownedSessions.filter(session => !session.projectId || !projectIds.has(session.projectId))
  if (ungroupedSessions.length) groups.push({ id: 'ungrouped', name: 'Ungrouped', sessions: ungroupedSessions })

  return groups
}
