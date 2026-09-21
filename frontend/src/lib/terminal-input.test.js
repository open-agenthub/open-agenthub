import { describe, expect, it } from 'vitest'
import { SUBMIT_ENTER_DELAY_MS, submitToAgent } from './terminal-input.js'

describe('submitToAgent', () => {
  it('sends the text first and the Enter as its own delayed frame', () => {
    const frames = []
    const scheduled = []
    const ok = submitToAgent(
      value => frames.push(value),
      'fix the failing test',
      (fn, ms) => scheduled.push({ fn, ms })
    )

    expect(ok).toBe(true)
    expect(frames).toEqual([{ type: 'input', data: 'fix the failing test' }])
    expect(scheduled).toHaveLength(1)
    expect(scheduled[0].ms).toBe(SUBMIT_ENTER_DELAY_MS)

    scheduled[0].fn()
    expect(frames[1]).toEqual({ type: 'input', data: '\r' })
    // A carriage return bundled with the text would be swallowed as part of
    // the paste by the agent TUI, forcing the user to press Enter again.
    expect(frames[0].data).not.toContain('\r')
  })

  it('ignores an empty composer', () => {
    const frames = []
    expect(submitToAgent(value => frames.push(value), '', () => {})).toBe(false)
    expect(frames).toEqual([])
  })
})
