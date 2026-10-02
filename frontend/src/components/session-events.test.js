// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionWorkspace from './SessionWorkspace.vue'

const mocks = vi.hoisted(() => ({
  capabilities: vi.fn(), list: vi.fn(), presentation: vi.fn(), dismiss: vi.fn(),
  open: vi.fn(), close: vi.fn()
}))

vi.mock('../api.js', () => ({
  api: {
    sessionFileCapabilities: mocks.capabilities,
    listSessionFiles: mocks.list,
    getFilePresentation: mocks.presentation,
    setFilePresentation: mocks.dismiss,
    reserveSessionFile: vi.fn(),
    uploadSessionFile: vi.fn(),
    completeSessionFile: vi.fn(),
    deleteSessionFile: vi.fn()
  },
  getSharedFileCapabilities: mocks.capabilities,
  listSharedSessionFiles: mocks.list,
  getSharedFilePresentation: mocks.presentation,
  sessionEventsUrl: id => `wss://host/ws/sessions/${id}/events`,
  sharedSessionEventsUrl: token => `wss://host/ws/shared/${token}/events`
}))

// Captures the callbacks the component hands the stream, so a push can be delivered without a
// socket and the connection state can be driven from the test.
vi.mock('../lib/live.js', () => ({
  openSessionEvents: (resolveUrl, onEvent, onConnectedChange) => {
    mocks.open({ resolveUrl, onEvent, onConnectedChange })
    return { close: mocks.close }
  }
}))

function stream() {
  return mocks.open.mock.calls.at(-1)[0]
}

// Tracked so every test tears its workspace down. A component left mounted keeps its poll timer
// running into the next test, and the counts these assertions rest on then come from two
// workspaces at once — which is exactly how a passing cadence assertion first looked like a
// product bug here.
const mounted = []

function mountWorkspace(props = {}) {
  const wrapper = mount(SessionWorkspace, {
    props: { session: { id: 's1', browser: { phase: 'Stopped' } }, canWrite: true, ...props },
    global: { stubs: { BrowserPane: true } }
  })
  mounted.push(wrapper)
  return wrapper
}

describe('session event push', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers()
    mocks.capabilities.mockResolvedValue({ limits: { presentationPollMilliseconds: 1500 } })
    mocks.list.mockResolvedValue([{ id: 'f1', name: 'shot.png', mimeType: 'image/png', size: 12, state: 'Ready' }])
    mocks.presentation.mockResolvedValue({ revision: 1, fileId: null })
  })
  afterEach(() => {
    while (mounted.length) mounted.pop().unmount()
    vi.useRealTimers()
  })

  it('re-reads the presentation when the backend pushes an event', async () => {
    mountWorkspace()
    await flushPromises()
    const before = mocks.presentation.mock.calls.length

    stream().onEvent('files')
    await flushPromises()

    expect(mocks.presentation.mock.calls.length).toBe(before + 1)
  })

  /// The whole point of the socket: with it up, the timer must not keep hitting the backend at
  /// the old cadence. A regression here is invisible in the UI and only shows up as load.
  it('stops polling at the fast cadence while the socket is connected', async () => {
    mountWorkspace()
    await flushPromises()
    stream().onConnectedChange(true)
    await flushPromises()
    const before = mocks.presentation.mock.calls.length

    await vi.advanceTimersByTimeAsync(20_000)
    await flushPromises()

    expect(mocks.presentation.mock.calls.length).toBe(before)
  })

  it('still polls at the fallback interval, so a missed event cannot strand the pane', async () => {
    mountWorkspace()
    await flushPromises()
    stream().onConnectedChange(true)
    await flushPromises()
    const before = mocks.presentation.mock.calls.length

    await vi.advanceTimersByTimeAsync(31_000)
    await flushPromises()

    expect(mocks.presentation.mock.calls.length).toBeGreaterThan(before)
  })

  it('returns to the fast poll when the socket drops', async () => {
    mountWorkspace()
    await flushPromises()
    stream().onConnectedChange(true)
    await flushPromises()
    stream().onConnectedChange(false)
    await flushPromises()
    const before = mocks.presentation.mock.calls.length

    // Under the slow fallback this window would produce nothing at all.
    await vi.advanceTimersByTimeAsync(5_000)
    await flushPromises()

    expect(mocks.presentation.mock.calls.length).toBeGreaterThan(before)
  })

  it('closes the stream when the workspace goes away', async () => {
    const wrapper = mountWorkspace()
    await flushPromises()

    wrapper.unmount()

    expect(mocks.close).toHaveBeenCalled()
  })

  it('subscribes through the share token when one is given', async () => {
    mountWorkspace({ sharedToken: 'tok' })
    await flushPromises()

    expect(await stream().resolveUrl()).toBe('wss://host/ws/shared/tok/events')
  })
})
