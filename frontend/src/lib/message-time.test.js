import { describe, expect, it } from 'vitest'
import { fullMessageTime, isoMessageTime, shortMessageTime } from './message-time.js'
import { toTranscriptItems } from './transcript.js'

const now = new Date(2026, 9, 18, 17, 30)
const opts = { now, locale: 'en-US' }

describe('message times (#147)', () => {
  it('shows only the time for today, the day for this year, the year for older', () => {
    expect(shortMessageTime(new Date(2026, 9, 18, 9, 5), opts)).toBe('09:05 AM')
    expect(shortMessageTime(new Date(2026, 9, 17, 23, 59), opts)).toBe('Oct 17, 11:59 PM')
    expect(shortMessageTime(new Date(2025, 11, 31, 8, 0), opts)).toBe('Dec 31, 2025, 08:00 AM')
  })

  it('gives the full date for the hover, and nothing for a missing or broken time', () => {
    expect(fullMessageTime(new Date(2026, 9, 18, 9, 5, 7), { locale: 'en-US' })).toMatch(/^Sunday, October 18, 2026.*9:05:07 AM$/)
    expect(shortMessageTime(null, opts)).toBe('')
    expect(shortMessageTime('not a date', opts)).toBe('')
    expect(fullMessageTime(undefined)).toBe('')
    expect(isoMessageTime('2026-10-18T16:51:41.128Z')).toBe('2026-10-18T16:51:41.128Z')
  })

  it('takes the provider timestamp of a native entry; terminal history has none', () => {
    const [entry] = toTranscriptItems({ source: 'native', entries: [{ role: 'user', text: 'hi', at: '2026-10-18T16:51:41.128Z' }] })
    expect(entry.at).toBe('2026-10-18T16:51:41.128Z')
    expect(toTranscriptItems({ source: 'native', entries: [{ role: 'assistant', text: 'x' }] })[0].at).toBeNull()
    expect(toTranscriptItems({ source: 'scrollback', text: 'some output' })[0].at).toBeUndefined()
  })
})
