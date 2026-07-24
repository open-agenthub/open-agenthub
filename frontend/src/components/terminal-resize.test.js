// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import TerminalPane from './TerminalPane.vue'

const mocks = vi.hoisted(() => ({ terminals: [], sockets: [], observers: [], dims: null }))

vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    constructor(options) { this.options = options; this.cols = 80; this.rows = 24; this.resizeCalls = []; mocks.terminals.push(this) }
    loadAddon() {} open() {} write() {} dispose() {}
    onData(callback) { this.input = callback }
    resize(cols, rows) { this.cols = cols; this.rows = rows; this.resizeCalls.push([cols, rows]) }
  }
}))
vi.mock('@xterm/addon-fit', () => ({
  FitAddon: class {
    fit() {}
    proposeDimensions() { return mocks.dims }
  }
}))
vi.mock('../api.js', () => ({
  api: { getTranscript: vi.fn().mockResolvedValue('') },
  terminalUrl: vi.fn().mockResolvedValue('ws://terminal'),
  shellUrl: vi.fn().mockResolvedValue('ws://shell'),
  sharedTerminalUrl: vi.fn().mockReturnValue('ws://shared'),
  getSharedTranscript: vi.fn().mockResolvedValue('')
}))

class MockSocket {
  static OPEN = 1
  static CLOSING = 2
  constructor(url) { this.url = url; this.readyState = MockSocket.OPEN; this.sent = []; mocks.sockets.push(this) }
  send(value) { this.sent.push(JSON.parse(value)) }
  close() {}
}
class MockResizeObserver {
  constructor(callback) { this.callback = callback; mocks.observers.push(this) }
  observe() {} disconnect() {}
}

let rafQueue
function runFrame() {
  const callbacks = rafQueue
  rafQueue = []
  callbacks.forEach(cb => cb && cb())
}

function setHostSize(wrapper, width, height) {
  const el = wrapper.get('.term').element
  Object.defineProperty(el, 'clientWidth', { configurable: true, value: width })
  Object.defineProperty(el, 'clientHeight', { configurable: true, value: height })
}

async function mountRunningPane() {
  const wrapper = mount(TerminalPane, { props: { session: { id: 's1', phase: 'Running' } } })
  await Promise.resolve(); await Promise.resolve()
  mocks.sockets[0].onopen()
  return wrapper
}

describe('terminal fit/resize stability', () => {
  beforeEach(() => {
    mocks.terminals.length = 0
    mocks.sockets.length = 0
    mocks.observers.length = 0
    mocks.dims = null
    rafQueue = []
    globalThis.WebSocket = MockSocket
    globalThis.ResizeObserver = MockResizeObserver
    globalThis.requestAnimationFrame = cb => rafQueue.push(cb)
    globalThis.cancelAnimationFrame = () => {}
  })

  it('sends the initial size once on socket open', async () => {
    await mountRunningPane()
    expect(mocks.sockets[0].sent).toEqual([{ type: 'resize', cols: 80, rows: 24 }])
  })

  it('coalesces observer bursts into a single fit per animation frame', async () => {
    const wrapper = await mountRunningPane()
    setHostSize(wrapper, 800, 600)
    mocks.dims = { cols: 100, rows: 30 }

    for (let i = 0; i < 5; i++) mocks.observers[0].callback()
    expect(rafQueue).toHaveLength(1)
    runFrame()

    expect(mocks.terminals[0].resizeCalls).toEqual([[100, 30]])
    expect(mocks.sockets[0].sent.filter(f => f.type === 'resize')).toEqual([
      { type: 'resize', cols: 80, rows: 24 },
      { type: 'resize', cols: 100, rows: 30 }
    ])
  })

  it('does not resize or send frames when proposed dimensions are unchanged', async () => {
    const wrapper = await mountRunningPane()
    setHostSize(wrapper, 800, 600)
    mocks.dims = { cols: 100, rows: 30 }
    mocks.observers[0].callback()
    runFrame()

    mocks.observers[0].callback()
    runFrame()
    mocks.observers[0].callback()
    runFrame()

    expect(mocks.terminals[0].resizeCalls).toEqual([[100, 30]])
    expect(mocks.sockets[0].sent.filter(f => f.type === 'resize')).toHaveLength(2)
  })

  it('skips fitting while the host has no size (hidden pane)', async () => {
    const wrapper = await mountRunningPane()
    setHostSize(wrapper, 0, 0)
    mocks.dims = { cols: 100, rows: 30 }
    mocks.observers[0].callback()
    runFrame()

    expect(mocks.terminals[0].resizeCalls).toEqual([])

    setHostSize(wrapper, 800, 600)
    await wrapper.setProps({ session: { id: 's1', phase: 'Running' }, active: true })
    mocks.observers[0].callback()
    runFrame()
    expect(mocks.terminals[0].resizeCalls).toEqual([[100, 30]])
  })

  it('ignores invalid proposed dimensions', async () => {
    const wrapper = await mountRunningPane()
    setHostSize(wrapper, 800, 600)
    mocks.dims = { cols: NaN, rows: NaN }
    mocks.observers[0].callback()
    runFrame()

    expect(mocks.terminals[0].resizeCalls).toEqual([])
    expect(mocks.sockets[0].sent.filter(f => f.type === 'resize')).toHaveLength(1)
  })

  it('refits when the pane becomes active again', async () => {
    const wrapper = await mountRunningPane()
    setHostSize(wrapper, 800, 600)
    mocks.dims = { cols: 120, rows: 40 }

    await wrapper.setProps({ session: { id: 's1', phase: 'Running' }, active: false })
    await wrapper.setProps({ session: { id: 's1', phase: 'Running' }, active: true })
    runFrame()

    expect(mocks.terminals[0].resizeCalls).toEqual([[120, 40]])
  })
})
