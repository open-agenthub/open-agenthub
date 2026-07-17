import { describe, expect, it } from 'vitest'
import { groupSessions } from './projects.js'

describe('empty project visibility', () => {
  it('keeps empty projects until a search is active', () => {
    const projects = [{ id: 'p1', name: 'Empty', sortOrder: 0 }]

    expect(groupSessions(projects, [], '')).toEqual([
      { id: 'p1', name: 'Empty', color: undefined, sessions: [] }
    ])
    expect(groupSessions(projects, [], 'missing')).toEqual([])
  })
})
