// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import BrowserPane from './BrowserPane.vue'
import SessionWorkspace from './SessionWorkspace.vue'
import { browserUrl, sharedBrowserUrl } from '../api.js'

const novnc = vi.hoisted(() => ({ instances: [] }))
vi.mock('@novnc/novnc', () => ({ default: class {
  constructor(host, url) { this.host = host; this.url = url; this.listeners = {}; novnc.instances.push(this) }
  addEventListener(name, callback) { this.listeners[name] = callback }
  disconnect() { this.disconnected = true }
} }))

describe('browser workspace', () => {
  beforeEach(() => { novnc.instances.length = 0 })
  it('does not render browser chrome while stopped', () => {
    const wrapper = mount(SessionWorkspace, { props: { session: { id: 's1', browser: { phase: 'Stopped' } }, canWrite: true }, slots: { default: '<div data-terminal>terminal</div>' } })
    expect(wrapper.find('[data-browser-pane]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Start browser')
  })
  it('opens the 50/50 split when the agent starts a browser', () => {
    const wrapper = mount(SessionWorkspace, { props: { session: { id: 's1', browser: { phase: 'Running' } }, canWrite: true }, slots: { default: '<div data-terminal>terminal</div>' } })
    expect(wrapper.get('[data-browser-pane]').exists()).toBe(true)
    expect(wrapper.get('[data-session-split]').attributes('style')).toContain('50%')
  })
  it('sets noVNC viewOnly for viewers', async () => {
    mount(BrowserPane, { props: { session: { id: 's1', browser: { phase: 'Running' } }, canWrite: false } })
    await flushPromises()
    expect(novnc.instances[0].viewOnly).toBe(true)
    expect(novnc.instances[0].scaleViewport).toBe(true)
    expect(novnc.instances[0].resizeSession).toBe(false)
  })
  it('uses authenticated and shared browser websocket routes', async () => {
    expect(await browserUrl('session / one')).toContain('/ws/sessions/session%20%2F%20one/browser')
    expect(sharedBrowserUrl('share / one')).toContain('/ws/shared/share%20%2F%20one/browser')
  })
  it('clamps keyboard resizing to the 25–75 percent range', async () => {
    const wrapper = mount(SessionWorkspace, { props: { session: { id: 's1', browser: { phase: 'Running' } }, canWrite: true }, slots: { default: '<div data-terminal>terminal</div>' }, global: { stubs: { BrowserPane: true } } })
    const separator = wrapper.get('[role="separator"]')
    for (let index = 0; index < 20; index += 1) await separator.trigger('keydown', { key: 'ArrowLeft' })
    expect(separator.attributes('aria-valuenow')).toBe('25')
    for (let index = 0; index < 30; index += 1) await separator.trigger('keydown', { key: 'ArrowRight' })
    expect(separator.attributes('aria-valuenow')).toBe('75')
  })
})
