import { describe, it, expect } from 'vitest'
import { detectAlerts } from './desktop-notify.js'

// Session shape as stored by App.vue's refresh() (api.listSessions()):
// { id, title, phase: 'Running'|'Pending'|'Scheduled'|'Succeeded'|'Failed'|'Paused', questionPending, mode, ... }
const s = (id, over = {}) => ({ id: String(id), title: 't' + id, questionPending: false, phase: 'Running', ...over })

describe('detectAlerts', () => {
  it('fires once when a question becomes pending', () => {
    const prev = [s(1)], next = [s(1, { questionPending: true })]
    expect(detectAlerts(prev, next)).toEqual([{ id: '1', title: 't1', kind: 'question' }])
    // no repeat while the question is still pending
    expect(detectAlerts(next, next)).toEqual([])
  })

  it('fires on transitions into Succeeded and Failed', () => {
    const prev = [s(1), s(2)]
    const next = [s(1, { phase: 'Succeeded' }), s(2, { phase: 'Failed' })]
    expect(detectAlerts(prev, next).map(a => a.kind)).toEqual(['finished', 'failed'])
    // terminal phases do not re-fire on the next poll
    expect(detectAlerts(next, next)).toEqual([])
  })

  it('never alerts for sessions not in the previous snapshot', () => {
    expect(detectAlerts([], [s(1, { questionPending: true, phase: 'Failed' })])).toEqual([])
  })

  it('ignores sessions that disappeared', () => {
    expect(detectAlerts([s(1)], [])).toEqual([])
  })

  it('does not alert while a session keeps running', () => {
    expect(detectAlerts([s(1)], [s(1)])).toEqual([])
    expect(detectAlerts([s(1)], [s(1, { phase: 'Paused' })])).toEqual([])
  })
})
