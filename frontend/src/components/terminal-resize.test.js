// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import TerminalPane from './TerminalPane.vue'

const mocks = vi.hoisted(() => ({ terminals: [], sockets: [], observers: [], dims: null }))

vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    constructor(options) {
      this.written = []; this.options = options; this.cols = 80; this.rows = 24; this.resizeCalls = []
      this.lines = []
      this.buffer = { active: { baseY: 0, getLine: y => ({ translateToString: () => this.lines[y] || '' }) } }
      mocks.terminals.push(this)
    }
    loadAddon() {} open() {} write(data, callback) { this.written.push(data); callback?.() } dispose() {}
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

  it('consumes activity frames without rendering them as terminal text', async () => {
    const wrapper = await mountRunningPane()
    const socket = mocks.sockets[0]
    const frame = JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'working', source: 'hooks' })
    socket.onmessage({ data: frame })
    expect(wrapper.emitted('activity').at(-1)).toEqual([{ state: 'working', source: 'hooks' }])
    expect(mocks.terminals[0].written).not.toContain(frame)
    socket.onclose()
    expect(wrapper.emitted('activity').at(-1)[0].state).toBe('unknown')
    wrapper.unmount()
  })
  it('clears a menu from the live screen while output continues faster than the settling interval', async () => {
    vi.useFakeTimers()
    const wrapper = await mountRunningPane()
    try {
      const term = mocks.terminals[0], socket = mocks.sockets[0]
      term.lines = ['Select a mode', '› 1. First', '  2. Second', 'Enter to select']
      socket.onmessage({ data: 'menu redraw' })
      await vi.advanceTimersByTimeAsync(150)
      expect(wrapper.emitted('menu').at(-1)[0]?.kind).toBe('choice')
      // Selecting starts a command: spinner writes must not postpone reading the new screen.
      term.lines = ['Working…']
      for (let i = 0; i < 4; i++) {
        socket.onmessage({ data: 'spinner redraw' })
        await vi.advanceTimersByTimeAsync(50)
      }
      expect(wrapper.emitted('menu').at(-1)).toEqual([null])
      term.lines = ['Choose effort', '› 1. Low', '  2. High', 'Enter to select']
      socket.onmessage({ data: 'submenu redraw' })
      await vi.advanceTimersByTimeAsync(150)
      expect(wrapper.emitted('menu').at(-1)[0]?.title).toBe('Choose effort')
    } finally { wrapper.unmount(); vi.useRealTimers() }
  })

  it('uses bracketed paste and a delayed Enter on the existing connection for workspace messages', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = await mountRunningPane()
      const delivery = wrapper.vm.submitMessage('first line\nsecond line\u001b[201~')
      expect(mocks.sockets).toHaveLength(1)
      expect(mocks.sockets[0].sent.at(-1)).toEqual({ type: 'input', data: '\u001b[200~first line\nsecond line[201~\u001b[201~' })
      await vi.advanceTimersByTimeAsync(150)
      await delivery
      expect(mocks.sockets[0].sent.at(-1)).toEqual({ type: 'input', data: '\r' })
      wrapper.vm.interruptAgent()
      expect(mocks.sockets[0].sent.at(-1)).toEqual({ type: 'input', data: '\u0003' })
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  // #148: each attachment path is a paste of its own, ahead of the text, and Enter comes last.
  it('pastes attachment paths one by one before the text, then submits', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = await mountRunningPane()
      const socket = mocks.sockets[0]
      const delivery = wrapper.vm.submitMessage('What is this?', { paths: ['/workspace/a.png', '/workspace/b.png'] })
      await vi.advanceTimersByTimeAsync(150)
      await vi.advanceTimersByTimeAsync(150)
      await vi.advanceTimersByTimeAsync(150)
      await delivery
      expect(socket.sent.filter(value => value.type === 'input').map(value => value.data)).toEqual([
        '\u001b[200~/workspace/a.png\u001b[201~', '\u001b[200~/workspace/b.png\u001b[201~',
        '\u001b[200~ What is this?\u001b[201~', '\r'])
      const imageOnly = wrapper.vm.submitMessage('', { paths: ['/workspace/c.png'] })
      await vi.advanceTimersByTimeAsync(150)
      await imageOnly
      expect(socket.sent.slice(-2).map(value => value.data)).toEqual(['\u001b[200~/workspace/c.png\u001b[201~', '\r'])
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  it('cancels the pending Enter when the session switches', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = await mountRunningPane()
      const socket = mocks.sockets[0]
      const delivery = wrapper.vm.submitMessage('old prompt')
      const rejected = expect(delivery).rejects.toThrow('Connection changed')
      await wrapper.setProps({ session: { id: 's2', phase: 'Running' } })
      await rejected
      await vi.advanceTimersByTimeAsync(150)
      expect(socket.sent.filter(value => value.data === '\r')).toEqual([])
      expect(mocks.sockets[1].sent.filter(value => value.type === 'input')).toEqual([])
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  it('rejects workspace input and interrupts after write access is revoked', async () => {
    const wrapper = await mountRunningPane()
    await wrapper.setProps({ readonly: true })
    await expect(wrapper.vm.submitMessage('forbidden')).rejects.toThrow('disconnected')
    expect(() => wrapper.vm.interruptAgent()).toThrow('disconnected')
    expect(mocks.sockets[0].sent.filter(value => value.type === 'input')).toEqual([])
    wrapper.unmount()
  })

  it('sends the initial size once on socket open', async () => {
    await mountRunningPane()
    expect(mocks.sockets[0].sent).toEqual([{ type: 'subscribe', channel: 'activity' }, { type: 'resize', cols: 80, rows: 24 }])
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
