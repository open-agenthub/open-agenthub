import { describe, it, expect } from 'vitest'
import { LICENSE_SETTINGS_PATH, isLicenseError, licenseBadge, licenseBadgeLabel, seatOverbooked } from './license.js'

describe('isLicenseError', () => {
  it('recognises the 402 the api layer throws, by status or by code', () => {
    expect(isLicenseError(Object.assign(new Error('402 x'), { status: 402 }))).toBe(true)
    expect(isLicenseError({ code: 'license_required' })).toBe(true)
  })

  it('is false for other failures and for nothing at all', () => {
    expect(isLicenseError(Object.assign(new Error('403 x'), { status: 403 }))).toBe(false)
    expect(isLicenseError(new Error('boom'))).toBe(false)
    expect(isLicenseError(null)).toBe(false)
    expect(isLicenseError(undefined)).toBe(false)
  })

  it('points at the admin license tab', () => {
    expect(LICENSE_SETTINGS_PATH).toBe('/settings/license')
  })
})

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
