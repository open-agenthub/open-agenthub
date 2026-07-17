// @vitest-environment happy-dom
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import DuplicateSessionDialog from './DuplicateSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import ShareSessionDialog from './ShareSessionDialog.vue'
import TerminalPane from './TerminalPane.vue'

const mocks = vi.hoisted(() => ({
  terminals: [], sockets: [], observers: [],
  api: {
    updateSession: vi.fn(), duplicateSession: vi.fn(), listSessionShares: vi.fn(),
    createShareUser: vi.fn(), updateShareUser: vi.fn(), deleteShareUser: vi.fn(),
    createShareLink: vi.fn(), updateShareLink: vi.fn(), deleteShareLink: vi.fn(),
    updateMcpPolicy: vi.fn(), getTranscript: vi.fn().mockResolvedValue('history')
  }
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  terminalUrl: vi.fn(id => Promise.resolve(`ws://terminal/${id}`)),
  shellUrl: vi.fn(id => Promise.resolve(`ws://shell/${id}`)),
  sharedTerminalUrl: vi.fn(token => `ws://shared/${token}`),
  getSharedTranscript: vi.fn().mockResolvedValue('history')
}))
vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    constructor(options) { this.options = options; this.cols = 80; this.rows = 24; mocks.terminals.push(this) }
    loadAddon() {} open() {} write() {} dispose() { this.disposed = true }
    onData(callback) { this.input = callback }
  }
}))
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class { fit() {} } }))

class MockSocket {
  static OPEN = 1
  static CLOSING = 2
  constructor(url) { this.url = url; this.readyState = MockSocket.OPEN; this.sent = []; mocks.sockets.push(this) }
  send(value) { this.sent.push(JSON.parse(value)) }
  close() { this.closed = true; this.readyState = MockSocket.CLOSING }
}
class MockResizeObserver {
  constructor(callback) { this.callback = callback; mocks.observers.push(this) }
  observe() {} disconnect() { this.disconnected = true }
}

const session = (id, title = id, phase = 'Running') => ({ id, title, phase, mode: 'Interactive' })

describe('component lifecycle regressions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.terminals.length = 0; mocks.sockets.length = 0; mocks.observers.length = 0
    mocks.api.listSessionShares.mockResolvedValue({ users: [], links: [], policy: {} })
    globalThis.WebSocket = MockSocket
    globalThis.ResizeObserver = MockResizeObserver
  })
  afterEach(() => vi.useRealTimers())

  it('reconnects the terminal when the session id changes', async () => {
    const wrapper = mount(TerminalPane, { props: { session: session('s1') } })
    await flushPromises()
    await wrapper.setProps({ session: session('s2') })
    await flushPromises()

    expect(mocks.sockets).toHaveLength(2)
    expect(mocks.sockets[0].closed).toBe(true)
    expect(mocks.sockets[1].url).toBe('ws://terminal/s2')
  })

  it('reconnects and enables input when a paused session resumes', async () => {
    const wrapper = mount(TerminalPane, { props: { session: session('s1', 'One', 'Paused') } })
    await flushPromises()
    expect(mocks.sockets).toHaveLength(0)

    await wrapper.setProps({ session: session('s1', 'One', 'Running') })
    await flushPromises()

    expect(mocks.sockets).toHaveLength(1)
    expect(mocks.terminals[0].input).toBeTypeOf('function')
  })

  it('cancels pending reconnects when unmounted', async () => {
    vi.useFakeTimers()
    const wrapper = mount(TerminalPane, { props: { session: session('s1') } })
    await Promise.resolve(); await Promise.resolve()
    mocks.sockets[0].onclose()
    wrapper.unmount()
    await vi.runAllTimersAsync()

    expect(mocks.sockets).toHaveLength(1)
    expect(mocks.terminals[0].disposed).toBe(true)
    expect(mocks.observers[0].disconnected).toBe(true)
  })

  it('resets edit form state when the target session changes', async () => {
    const wrapper = mount(EditSessionDialog, { props: { session: session('s1', 'First'), projects: [] }, global: { stubs: { RepoPicker: true } } })
    await wrapper.setProps({ session: session('s2', 'Second') })
    expect(wrapper.get('input').element.value).toBe('Second')
  })

  it('resets duplicate form state when the target session changes', async () => {
    const wrapper = mount(DuplicateSessionDialog, { props: { session: session('s1', 'First'), projects: [] } })
    await wrapper.setProps({ session: session('s2', 'Second') })
    expect(wrapper.get('input').element.value).toBe('Copy of Second')
  })

  it('reloads shares when the target session changes', async () => {
    const wrapper = mount(ShareSessionDialog, { props: { session: session('s1') } })
    await flushPromises()
    mocks.api.listSessionShares.mockClear()
    await wrapper.setProps({ session: session('s2') })
    await flushPromises()
    expect(mocks.api.listSessionShares).toHaveBeenCalledWith('s2')
  })
  it('ignores stale share responses after the target session changes', async () => {
    let resolveFirst
    let resolveSecond
    mocks.api.listSessionShares
      .mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveSecond = resolve }))

    const wrapper = mount(ShareSessionDialog, { props: { session: session('s1') } })
    await Promise.resolve()
    await wrapper.setProps({ session: session('s2') })
    resolveSecond({ users: [], links: [{ id: 'new-link', role: 'Viewer' }], policy: {} })
    await flushPromises()
    resolveFirst({ users: [], links: [{ id: 'old-link', role: 'Viewer' }], policy: {} })
    await flushPromises()

    expect(wrapper.text()).toContain('new-link')
    expect(wrapper.text()).not.toContain('old-link')
  })

})
