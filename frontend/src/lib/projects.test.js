import { describe, expect, it } from 'vitest'
import { groupSessions } from './projects.js'

describe('groupSessions', () => {
  const projects = [
    { id: 'p2', name: 'Later', sortOrder: 2 },
    { id: 'p1', name: 'Core', sortOrder: 0 }
  ]
  const sessions = [
    { id: 's1', title: 'API', projectId: 'p1' },
    { id: 's2', title: 'Notes', projectId: null },
    { id: 's3', title: 'Shared work', accessRole: 'Viewer' },
    { id: 's4', title: 'Archive', projectId: 'p2' }
  ]

  it('groups matching sessions and preserves ungrouped', () => {
    const groups = groupSessions(projects, sessions, '')

    expect(groups.map(group => [group.name, group.sessions.map(session => session.id)])).toEqual([
      ['Core', ['s1']],
      ['Later', ['s4']],
      ['Shared with me', ['s3']],
      ['Ungrouped', ['s2']]
    ])
  })

  it('filters across project groups and hides empty groups', () => {
    const groups = groupSessions(projects, sessions, 'notes')

    expect(groups.map(group => [group.name, group.sessions.map(session => session.id)])).toEqual([
      ['Ungrouped', ['s2']]
    ])
  })

  it('keeps sessions for missing projects under ungrouped', () => {
    const groups = groupSessions([], [{ id: 's1', title: 'Orphan', projectId: 'deleted' }], '')

    expect(groups.map(group => [group.name, group.sessions.map(session => session.id)])).toEqual([
      ['Ungrouped', ['s1']]
    ])
  })
})
