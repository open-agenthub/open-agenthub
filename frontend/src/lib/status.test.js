import { describe, it, expect } from 'vitest'
import { phaseColor, canPause, tabLabel, PHASE_COLORS } from './status.js'

describe('phaseColor', () => {
  it('maps known phases', () => {
    expect(phaseColor('Running')).toBe('var(--running)')
    expect(phaseColor('Paused')).toBe('var(--paused)')
    expect(phaseColor('Failed')).toBe('var(--danger)')
  })
  it('falls back to muted for unknown phases', () => {
    expect(phaseColor('Whatever')).toBe('var(--muted)')
    expect(phaseColor(undefined)).toBe('var(--muted)')
  })
  it('has a colour for every documented phase', () => {
    for (const p of ['Running', 'Pending', 'Scheduled', 'Succeeded', 'Failed', 'Paused'])
      expect(PHASE_COLORS[p]).toBeTruthy()
  })
})

describe('canPause', () => {
  it('allows pausing a running interactive session', () => {
    expect(canPause({ mode: 'Interactive', phase: 'Running' })).toBe(true)
    expect(canPause({ mode: 'Autonomous', phase: 'Pending' })).toBe(true)
  })
  it('rejects finished, paused or scheduled sessions', () => {
    expect(canPause({ mode: 'Interactive', phase: 'Paused' })).toBe(false)
    expect(canPause({ mode: 'Interactive', phase: 'Succeeded' })).toBe(false)
    expect(canPause({ mode: 'Scheduled', phase: 'Running' })).toBe(false)
  })
  it('handles null', () => expect(canPause(null)).toBe(false))
})

describe('tabLabel', () => {
  it('labels known kinds', () => {
    expect(tabLabel('agent')).toBe('Agent')
    expect(tabLabel('shell')).toBe('Shell')
  })
  it('passes through unknown kinds', () => expect(tabLabel('foo')).toBe('foo'))
})
