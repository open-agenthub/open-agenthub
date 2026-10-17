// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import BrowserPane from './BrowserPane.vue'
import SessionWorkspace from './SessionWorkspace.vue'
import { browserUrl, sharedBrowserUrl } from '../api.js'

vi.mock('../api.js', async importOriginal => {
  const actual = await importOriginal()
  return {
    ...actual,
    api: {
      ...actual.api,
      sessionFileCapabilities: vi.fn().mockResolvedValue({ limits: { presentationPollMilliseconds: 60_000 } }),
      listSessionFiles: vi.fn().mockResolvedValue([]),
      getFilePresentation: vi.fn().mockResolvedValue(null)
    }
  }
})
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
  // Issue #110: once open, the pane could only be closed by the agent or a reload.
  describe('closing the browser pane', () => {
    const mountRunning = () => mount(SessionWorkspace, {
      props: { session: { id: 's1', browser: { phase: 'Running' } }, canWrite: true },
      slots: { default: '<div data-terminal>terminal</div>' }, global: { stubs: { BrowserPane: true } }, attachTo: document.body
    })
    it('hides the pane without touching the lease and reopens it on request', async () => {
      const wrapper = mountRunning()
      await wrapper.get('[data-browser-close]').trigger('click')
      expect(wrapper.find('[data-session-split]').exists()).toBe(false)
      expect(wrapper.find('[data-terminal]').exists()).toBe(true)
      expect(novnc.instances.every(instance => !instance.disconnected)).toBe(true)
      wrapper.vm.openBrowser()
      await flushPromises()
      expect(wrapper.find('[data-workspace-tab="browser"]').exists()).toBe(true)
      wrapper.unmount()
    })
    it('closes on Escape from the pane chrome but not from the remote canvas', async () => {
      const wrapper = mount(SessionWorkspace, {
        props: { session: { id: 's1', browser: { phase: 'Running' } }, canWrite: true },
        slots: { default: '<div data-terminal>terminal</div>' }, global: { stubs: { BrowserPane: { template: '<section><div data-browser-canvas tabindex="0"></div></section>' } } }
      })
      await wrapper.get('[data-browser-canvas]').trigger('keydown', { key: 'Escape' })
      expect(wrapper.find('[data-workspace-tab="browser"]').exists()).toBe(true)
      await wrapper.get('[data-workspace-tab="browser"]').trigger('keydown', { key: 'Escape' })
      expect(wrapper.find('[data-workspace-tab="browser"]').exists()).toBe(false)
    })
    it('shows a new lease again after the hidden one stopped', async () => {
      const wrapper = mountRunning()
      await wrapper.get('[data-browser-close]').trigger('click')
      await wrapper.setProps({ session: { id: 's1', browser: { phase: 'Stopped' } } })
      await wrapper.setProps({ session: { id: 's1', browser: { phase: 'Running' } } })
      expect(wrapper.find('[data-workspace-tab="browser"]').exists()).toBe(true)
      wrapper.unmount()
    })
  })
})
