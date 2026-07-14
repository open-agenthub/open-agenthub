import { describe, it, expect } from 'vitest'
import { licenseBadge, licenseBadgeLabel, seatOverbooked } from './license.js'

describe('licenseBadge', () => {
  it('is off when there is no license at all', () => {
    expect(licenseBadge(null)).toBe('off')
    expect(licenseBadge({ valid: false, present: false })).toBe('off')
    expect(licenseBadgeLabel({ valid: false, present: false })).toBe('not activated')
  })

  it('is invalid when a token is present but does not verify', () => {
    expect(licenseBadge({ valid: false, present: true })).toBe('invalid')
    expect(licenseBadgeLabel({ valid: false, present: true })).toBe('invalid')
  })

  it('is active for a valid license', () => {
    expect(licenseBadge({ valid: true, present: true })).toBe('active')
    expect(licenseBadgeLabel({ valid: true, present: true })).toBe('active')
  })
})

describe('seatOverbooked', () => {
  it('is false without a seat cap (unlimited / no license)', () => {
    expect(seatOverbooked({ used: 99, included: 0 })).toBe(false)
    expect(seatOverbooked(null)).toBe(false)
  })

  it('is false while within the cap', () => {
    expect(seatOverbooked({ used: 3, included: 5 })).toBe(false)
    expect(seatOverbooked({ used: 5, included: 5 })).toBe(false)
  })

  it('is true when seats in use exceed the cap', () => {
    expect(seatOverbooked({ used: 6, included: 5 })).toBe(true)
  })
})
