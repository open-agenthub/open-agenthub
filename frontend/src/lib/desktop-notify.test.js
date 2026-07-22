// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, it, expect } from 'vitest'
import { detectAlerts, showAlert } from './desktop-notify.js'

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

  it('ignores unexpected phases that happen to be Object.prototype keys', () => {
    expect(detectAlerts([s(1)], [s(1, { phase: 'constructor' })])).toEqual([])
  })
})

describe('showAlert', () => {
  let created

  function stubNotification({ permission = 'granted', throws = false } = {}) {
    created = []
    globalThis.Notification = class {
      static permission = permission
      constructor(title, options) {
        if (throws) throw new Error('Illegal constructor')
        created.push({ title, ...options })
      }
      close() {}
    }
  }

  const setHidden = hidden =>
    Object.defineProperty(document, 'hidden', { value: hidden, configurable: true })

  beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('desktopNotify', '1')
    stubNotification()
    setHidden(true)
  })
  afterEach(() => { delete globalThis.Notification })

  it('constructs a tagged notification when enabled, granted and hidden', () => {
    showAlert({ id: '7', title: 'deploy', kind: 'question' })
    expect(created).toEqual([{ title: 'Open AgentHub', body: 'Session deploy is waiting for your reply.', tag: 'agenthub-7' }])
  })

  it('does nothing while the tab is visible', () => {
    setHidden(false)
    showAlert({ id: '7', title: 'deploy', kind: 'finished' })
    expect(created).toEqual([])
  })

  it('does nothing when disabled or permission is missing', () => {
    localStorage.setItem('desktopNotify', '0')
    showAlert({ id: '7', title: 'deploy', kind: 'finished' })
    stubNotification({ permission: 'denied' })
    localStorage.setItem('desktopNotify', '1')
    showAlert({ id: '7', title: 'deploy', kind: 'finished' })
    expect(created).toEqual([])
  })

  it('never throws when the Notification constructor throws', () => {
    stubNotification({ throws: true })
    expect(() => showAlert({ id: '7', title: 'deploy', kind: 'failed' })).not.toThrow()
  })

  it('falls back to the id when a session has no title', () => {
    showAlert({ id: '9', title: '', kind: 'failed' })
    expect(created[0].body).toBe('Session 9 failed.')
  })
})
