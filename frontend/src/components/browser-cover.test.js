// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import BrowserPane from './BrowserPane.vue'

const state = vi.hoisted(() => ({ instances: [], observers: [] }))
vi.mock('@novnc/novnc', () => ({
  default: class {
    constructor(host, url) {
      this.host = host
      this.url = url
      this.listeners = {}
      this._display = { width: 1440, height: 900, scale: 1 }
      state.instances.push(this)
    }
    addEventListener(name, callback) { this.listeners[name] = callback }
    disconnect() { this.disconnected = true }
  }
}))

let originalResizeObserver

describe('browser cover scaling', () => {
  beforeEach(() => {
    state.instances.length = 0
    state.observers.length = 0
    originalResizeObserver = globalThis.ResizeObserver
    globalThis.ResizeObserver = class {
      constructor(callback) {
        this.callback = callback
        state.observers.push(this)
      }
      observe(element) { this.element = element }
      disconnect() { this.disconnected = true }
    }
  })

  afterEach(() => {
    globalThis.ResizeObserver = originalResizeObserver
  })

  it('uses the larger ratio to fill and crop the browser pane', async () => {
    const wrapper = mount(BrowserPane, {
      props: {
        session: { id: 's1', browser: { phase: 'Running' } },
        canWrite: true
      }
    })
    await flushPromises()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 800, 800)
    const instance = state.instances[0]

    instance.listeners.connect()

    expect(instance.scaleViewport).toBe(false)
    expect(instance._display.scale).toBeCloseTo(800 / 900)
  })

  it('reapplies cover scaling on resize and disconnects the observer', async () => {
    const wrapper = mount(BrowserPane, {
      props: {
        session: { id: 's1', browser: { phase: 'Running' } },
        canWrite: true
      }
    })
    await flushPromises()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 800, 800)
    const instance = state.instances[0]
    instance.listeners.connect()

    expect(state.observers).toHaveLength(1)
    expect(state.observers[0].element).toBe(host)

    setSize(host, 1200, 600)
    state.observers[0].callback()
    expect(instance._display.scale).toBeCloseTo(1200 / 1440)

    wrapper.unmount()
    expect(state.observers[0].disconnected).toBe(true)
  })
})

function setSize(element, width, height) {
  Object.defineProperties(element, {
    clientWidth: { configurable: true, value: width },
    clientHeight: { configurable: true, value: height }
  })
}
