import { describe, expect, it } from 'vitest'
import { secretIdsChange, secretIdsPayload, secretOptions, secretSelectionFor } from './secrets.js'

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
