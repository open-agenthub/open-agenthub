import { describe, expect, it } from 'vitest'
import {
  autoDeleteForm, autoDeletePayload, formatRemaining, fromSeconds, isExpiringSoon, toSeconds
} from './expiry.js'

const T = Date.parse('2026-10-10T12:00:00Z')

describe('toSeconds / fromSeconds', () => {
  it('converts dialog values to seconds and back', () => {
    expect(toSeconds(12, 'hours')).toBe(43200)
    expect(toSeconds(3, 'days')).toBe(259200)
    expect(toSeconds(1.5, 'hours')).toBe(5400)
    expect(toSeconds(0, 'hours')).toBeNull()
    expect(toSeconds('x', 'days')).toBeNull()
    expect(toSeconds(2, 'weeks')).toBeNull()
    expect(fromSeconds(259200)).toEqual({ amount: 3, unit: 'days' })
    expect(fromSeconds(43200)).toEqual({ amount: 12, unit: 'hours' })
    // An API-made "90m" shows as 1.5 h rather than being rounded to a whole hour.
    expect(fromSeconds(5400)).toEqual({ amount: 1.5, unit: 'hours' })
    expect(fromSeconds(0)).toEqual({ amount: 24, unit: 'hours' })
  })
})

describe('autoDeleteForm / autoDeletePayload', () => {
  it('prefills from a session and defaults to off with 24 h after last activity', () => {
    expect(autoDeleteForm({ autoDeleteAfterSeconds: 172800, autoDeleteFrom: 'start' }))
      .toEqual({ enabled: true, amount: 2, unit: 'days', from: 'start' })
    expect(autoDeleteForm({})).toEqual({ enabled: false, amount: 24, unit: 'hours', from: 'lastActivity' })
    expect(autoDeleteForm({ mode: 'Scheduled' }).from).toBe('start')
    expect(autoDeleteForm(null).enabled).toBe(false)
  })

  it('sends seconds when on, and the caller-chosen "off" value when off', () => {
    expect(autoDeletePayload({ enabled: true, amount: 6, unit: 'hours', from: 'lastActivity' }))
      .toEqual({ autoDeleteAfterSeconds: 21600, autoDeleteFrom: 'lastActivity' })
    // A create omits the deadline; an edit has to say 0, which the backend reads as "switch off".
    expect(autoDeletePayload({ enabled: false, amount: 6, unit: 'hours', from: 'start' }))
      .toEqual({ autoDeleteAfterSeconds: null, autoDeleteFrom: null })
    expect(autoDeletePayload({ enabled: false }, { off: 0 }))
      .toEqual({ autoDeleteAfterSeconds: 0, autoDeleteFrom: null })
  })
})

describe('formatRemaining / isExpiringSoon', () => {
  const at = ms => new Date(T + ms).toISOString()

  it('formats the remaining time in the largest two units', () => {
    expect(formatRemaining(at(2 * 86400e3 + 3 * 3600e3 + 7 * 60e3), T)).toBe('2 d 3 h')
    expect(formatRemaining(at(2 * 86400e3), T)).toBe('2 d')
    expect(formatRemaining(at(5 * 3600e3 + 30 * 60e3), T)).toBe('5 h 30 min')
    expect(formatRemaining(at(5 * 3600e3), T)).toBe('5 h')
    expect(formatRemaining(at(45 * 60e3), T)).toBe('45 min')
    expect(formatRemaining(at(20e3), T)).toBe('< 1 min')
    expect(formatRemaining(at(-5e3), T)).toBe('expired')
    expect(formatRemaining(null, T)).toBe('')
    expect(formatRemaining('garbage', T)).toBe('')
  })

  it('flags the last hour as soon', () => {
    expect(isExpiringSoon(at(59 * 60e3), T)).toBe(true)
    expect(isExpiringSoon(at(-1), T)).toBe(true)
    expect(isExpiringSoon(at(61 * 60e3), T)).toBe(false)
    expect(isExpiringSoon(null, T)).toBe(false)
  })
})
