// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import BrowserPane from './BrowserPane.vue'

const novnc = vi.hoisted(() => ({ instances: [] }))
vi.mock('@novnc/novnc', () => ({
  default: class {
    constructor(host, url) {
      this.host = host
      this.url = url
      this.listeners = {}
      novnc.instances.push(this)
    }
    addEventListener(name, callback) { this.listeners[name] = callback }
    disconnect() { this.disconnected = true }
  }
}))

describe('browser connection stability', () => {
  beforeEach(() => { novnc.instances.length = 0 })

  it('retains the RFB connection when polling replaces unchanged session data', async () => {
    const wrapper = mount(BrowserPane, {
      props: {
        session: { id: 's1', title: 'initial', browser: { phase: 'Running' } },
        canWrite: true
      }
    })
    await flushPromises()

    await wrapper.setProps({
      session: { id: 's1', title: 'refreshed', browser: { phase: 'Running' } }
    })
    await flushPromises()

    expect(novnc.instances).toHaveLength(1)
    expect(novnc.instances[0].disconnected).not.toBe(true)
  })

  it('updates view-only mode without reconnecting', async () => {
    const wrapper = mount(BrowserPane, {
      props: {
        session: { id: 's1', browser: { phase: 'Running' } },
        canWrite: true
      }
    })
    await flushPromises()

    await wrapper.setProps({ canWrite: false })
    await flushPromises()

    expect(novnc.instances).toHaveLength(1)
    expect(novnc.instances[0].viewOnly).toBe(true)
  })

  it('reconnects when the session identity changes', async () => {
    const wrapper = mount(BrowserPane, {
      props: {
        session: { id: 's1', browser: { phase: 'Running' } },
        canWrite: true
      }
    })
    await flushPromises()
    const first = novnc.instances[0]

    await wrapper.setProps({
      session: { id: 's2', browser: { phase: 'Running' } }
    })
    await flushPromises()

    expect(first.disconnected).toBe(true)
    expect(novnc.instances).toHaveLength(2)
  })
})
