import { describe, expect, it } from 'vitest'
import { GRANT_LEVELS, secretIdsChange, secretIdsPayload, secretLevel, secretOptions, secretSelectionFor, withSecretLevel } from './secrets.js'

// The selection conventions of docs/session-secrets.md: null follows the project (nothing outside
// one), a list narrows, ['*'] goes back to following the project in an edit.
const stored = [
  { id: 'a', name: 'A_TOKEN', delivery: 'env' },
  { id: 'b', name: 'b.pem', delivery: 'file' },
  { id: 'c', name: 'C_TOKEN', delivery: 'env' }
]
const project = { id: 'p', secretIds: ['a', 'b'] }

describe('secret selection', () => {
  it('offers the project selection in a project and everything outside one', () => {
    expect(secretOptions(stored, project).map(s => s.id)).toEqual(['a', 'b'])
    expect(secretOptions(stored, null).map(s => s.id)).toEqual(['a', 'b', 'c'])
    expect(secretOptions(stored, { id: 'q' })).toEqual([])
  })

  it('ticks the whole project for null, nothing outside a project', () => {
    expect(secretSelectionFor(null, secretOptions(stored, project), true)).toEqual(['a', 'b'])
    expect(secretSelectionFor(null, stored, false)).toEqual([])
    expect(secretSelectionFor(['b', 'gone'], secretOptions(stored, project), true)).toEqual(['b'])
  })

  it('creates with null while everything in the project is ticked, a list once something is not', () => {
    const options = secretOptions(stored, project)
    expect(secretIdsPayload(['a', 'b'], options, true)).toBeNull()
    expect(secretIdsPayload(['b'], options, true)).toEqual(['b'])
    expect(secretIdsPayload([], options, true)).toEqual([])
    // Outside a project nothing ticked is null (nothing), anything ticked is that list.
    expect(secretIdsPayload([], stored, false)).toBeNull()
    expect(secretIdsPayload(['c'], stored, false)).toEqual(['c'])
  })

  it('edits send only what changed, and the wildcard to follow the project again', () => {
    const options = secretOptions(stored, project)
    expect(secretIdsChange(['a', 'b'], options, true, null)).toEqual({})
    expect(secretIdsChange(['a'], options, true, null)).toEqual({ secretIds: ['a'] })
    expect(secretIdsChange(['a', 'b'], options, true, ['a'])).toEqual({ secretIds: ['*'] })
    expect(secretIdsChange(['a'], options, true, ['a'])).toEqual({})
    expect(secretIdsChange([], stored, false, ['c'])).toEqual({ secretIds: [] })
    expect(secretIdsChange([], stored, false, null)).toEqual({})
    expect(secretIdsChange(['c'], stored, false, null)).toEqual({ secretIds: ['c'] })
    // A project that selects nothing offers no choice and changes nothing.
    expect(secretIdsChange([], [], true, [])).toEqual({})
  })
})

// #329: one three-way control shows and edits both stored fields. Every stored combination of
// selection and grant shows exactly one level, choosing the level already shown changes nothing (so
// an untouched dialog sends nothing), and each choice writes the documented state.
describe('one control for the selection and the grants', () => {
  const stored = [{ id: 'a' }, { id: 'b' }]
  const project = { id: 'p', secretIds: ['a'] }
  const grant = (inheritToChildren, source = 'granted') => [{ secretId: 'a', inheritToChildren, source }]
  // [where, stored secretIds, stored grants, level shown]
  const states = [
    ['outside', null, [], 'none'],
    ['outside', ['a'], [], 'children'],
    ['outside', null, grant(false), 'session'],
    ['outside', null, grant(true), 'children'],
    ['outside', ['a'], grant(false, 'requested'), 'children'],
    ['outside', ['a'], grant(true), 'children'],
    ['project', null, [], 'children'],
    ['project', [], [], 'none'],
    ['project', [], grant(false, 'requested'), 'session'],
    ['project', [], grant(true, 'inherited'), 'children'],
    ['project', null, grant(false), 'children']
  ]

  it.each(states)('%s, secretIds %j, grants %j shows %s, and choosing it again sends nothing', (where, secretIds, grants, shown) => {
    const inProject = where === 'project'
    const options = secretOptions(stored, inProject ? project : null)
    const selected = secretSelectionFor(secretIds, options, inProject)
    expect(secretLevel('a', selected, grants)).toBe(shown)
    const next = withSecretLevel('a', shown, { selected, grants, selectable: options.map(s => s.id) })
    expect(next.grants).toBe(grants)
    expect(secretIdsChange(next.selected, options, inProject, secretIds)).toEqual({})
  })

  it('every level a choice writes is the level shown afterwards', () => {
    for (const [where, secretIds, grants] of states) {
      const inProject = where === 'project'
      const options = secretOptions(stored, inProject ? project : null)
      const selected = secretSelectionFor(secretIds, options, inProject)
      for (const { value } of GRANT_LEVELS) {
        const next = withSecretLevel('a', value, { selected, grants, selectable: options.map(s => s.id) })
        expect(secretLevel('a', next.selected, next.grants)).toBe(value)
        // The other secret is never touched.
        expect(next.selected.includes('b')).toBe(selected.includes('b'))
        expect(next.grants.filter(g => g.secretId !== 'a')).toEqual(grants.filter(g => g.secretId !== 'a'))
      }
    }
  })

  it('writes the documented state for each choice', () => {
    const outside = { selected: [], grants: [], selectable: ['a', 'b'] }
    expect(withSecretLevel('a', 'session', outside)).toEqual({ selected: [], grants: [{ secretId: 'a', inheritToChildren: false }] })
    expect(withSecretLevel('a', 'children', outside)).toEqual({ selected: ['a'], grants: [] })
    // A secret the project does not select can only reach child sessions as an inheriting grant.
    expect(withSecretLevel('b', 'children', { selected: ['a'], grants: [], selectable: ['a'] }))
      .toEqual({ selected: ['a'], grants: [{ secretId: 'b', inheritToChildren: true }] })
    // "This session" takes it out of the selection, which would pass it on, and keeps the grant's origin.
    expect(withSecretLevel('a', 'session', { selected: ['a'], grants: grant(false, 'requested'), selectable: ['a'] }))
      .toEqual({ selected: [], grants: grant(false, 'requested') })
    expect(withSecretLevel('a', 'children', { selected: [], grants: grant(false, 'requested'), selectable: ['a'] }))
      .toEqual({ selected: [], grants: grant(true, 'requested') })
    expect(withSecretLevel('a', 'none', { selected: ['a'], grants: grant(true), selectable: ['a'] })).toEqual({ selected: [], grants: [] })
  })
})
