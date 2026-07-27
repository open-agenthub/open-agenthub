// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import BrowserPane from './BrowserPane.vue'

const state = vi.hoisted(() => ({
  instances: [],
  observers: [],
  resizeViewport: vi.fn()
}))
vi.mock('../api.js', () => ({
  browserUrl: vi.fn(async id => `ws://browser/${id}`),
  sharedBrowserUrl: vi.fn(token => `ws://shared/${token}`),
  resizeBrowserViewport: state.resizeViewport
}))
vi.mock('@novnc/novnc', () => ({
  default: class {
    constructor(host, url) {
      this.host = host
      this.url = url
      this.listeners = {}
      state.instances.push(this)
    }
    addEventListener(name, callback) { this.listeners[name] = callback }
    disconnect() { this.disconnected = true }
  }
}))

let originalResizeObserver

describe('browser viewport sizing', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    state.instances.length = 0
    state.observers.length = 0
    state.resizeViewport.mockReset()
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
    vi.useRealTimers()
    globalThis.ResizeObserver = originalResizeObserver
  })

  it('debounces the owner pane size and asks the pod for matching dimensions', async () => {
    const wrapper = await ownerPane()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 800, 700)
    state.observers[0].callback()

    await vi.advanceTimersByTimeAsync(249)
    expect(state.resizeViewport).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)

    expect(state.resizeViewport).toHaveBeenCalledOnce()
    expect(state.resizeViewport).toHaveBeenCalledWith('s1', 800, 700)
    expect(state.instances[0].scaleViewport).toBe(true)
    expect(state.instances[0].resizeSession).toBe(false)
    wrapper.unmount()
  })

  it('submits only the latest dimensions and suppresses successful duplicates', async () => {
    const wrapper = await ownerPane()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 700, 600)
    state.observers[0].callback()
    setSize(host, 900, 650)
    state.observers[0].callback()
    await vi.advanceTimersByTimeAsync(250)
    await flushPromises()

    expect(state.resizeViewport).toHaveBeenCalledTimes(1)
    expect(state.resizeViewport).toHaveBeenLastCalledWith('s1', 900, 650)

    state.observers[0].callback()
    await vi.advanceTimersByTimeAsync(250)
    expect(state.resizeViewport).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  it.each([
    { canWrite: false, sharedToken: null },
    { canWrite: true, sharedToken: 'shared-token' }
  ])('never resizes for viewers or shared links', async props => {
    const wrapper = mount(BrowserPane, {
      props: {
        session: { id: 's1', browser: { phase: 'Running' } },
        ...props
      }
    })
    await flushPromises()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 800, 700)
    state.observers[0].callback()
    await vi.advanceTimersByTimeAsync(300)

    expect(state.resizeViewport).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('ignores unsupported small panes', async () => {
    const wrapper = await ownerPane()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 479, 700)
    state.observers[0].callback()
    await vi.advanceTimersByTimeAsync(300)

    expect(state.resizeViewport).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('keeps the VNC connection and retries after a resize request fails', async () => {
    state.resizeViewport.mockRejectedValueOnce(new Error('unavailable'))
    const wrapper = await ownerPane()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 800, 700)
    state.observers[0].callback()
    await vi.advanceTimersByTimeAsync(250)
    await flushPromises()

    expect(state.instances).toHaveLength(1)
    expect(state.instances[0].disconnected).not.toBe(true)

    state.observers[0].callback()
    await vi.advanceTimersByTimeAsync(250)
    await flushPromises()
    expect(state.resizeViewport).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('cancels a pending resize when the pane is removed', async () => {
    const wrapper = await ownerPane()
    const host = wrapper.get('.browser-canvas').element
    setSize(host, 800, 700)
    state.observers[0].callback()

    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(300)

    expect(state.resizeViewport).not.toHaveBeenCalled()
    expect(state.observers[0].disconnected).toBe(true)
  })
})

async function ownerPane() {
  const wrapper = mount(BrowserPane, {
    props: {
      session: { id: 's1', browser: { phase: 'Running' } },
      canWrite: true
    }
  })
  await flushPromises()
  return wrapper
}

function setSize(element, width, height) {
  Object.defineProperties(element, {
    clientWidth: { configurable: true, value: width },
    clientHeight: { configurable: true, value: height }
  })
}
