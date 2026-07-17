// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import TerminalPane from './TerminalPane.vue'
import TerminalView from './TerminalView.vue'

const terminalMocks = vi.hoisted(() => ({ terminals: [], sockets: [], observers: [] }))

vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    constructor(options) { this.options = options; this.cols = 80; this.rows = 24; terminalMocks.terminals.push(this) }
    loadAddon() {} open() {} write() {} dispose() {}
    onData(callback) { this.input = callback }
  }
}))
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class { fit() {} } }))
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
  constructor(url) { this.url = url; this.readyState = MockSocket.OPEN; this.sent = []; terminalMocks.sockets.push(this) }
  send(value) { this.sent.push(JSON.parse(value)) }
  close() {}
}
class MockResizeObserver {
  constructor(callback) { this.callback = callback; terminalMocks.observers.push(this) }
  observe() {} disconnect() {}
}

describe('shared terminal capabilities', () => {
  beforeEach(() => {
    terminalMocks.terminals.length = 0
    terminalMocks.sockets.length = 0
    terminalMocks.observers.length = 0
    globalThis.WebSocket = MockSocket
    globalThis.ResizeObserver = MockResizeObserver
  })

  it('does not register viewer input or send viewer resize frames', async () => {
    mount(TerminalPane, { props: { session: { id: 's1', phase: 'Running' }, readonly: true } })
    await Promise.resolve(); await Promise.resolve()
    terminalMocks.sockets[0].onopen()
    terminalMocks.observers[0].callback()

    expect(terminalMocks.terminals[0].input).toBeUndefined()
    expect(terminalMocks.sockets[0].sent).toEqual([])
  })

  it('forwards collaborator input', async () => {
    mount(TerminalPane, { props: { session: { id: 's1', phase: 'Running' }, readonly: false } })
    await Promise.resolve(); await Promise.resolve()
    terminalMocks.terminals[0].input('hello')

    expect(terminalMocks.sockets[0].sent).toContainEqual({ type: 'input', data: 'hello' })
  })

  it('does not render a shell tab for collaborators', () => {
    const wrapper = mount(TerminalView, {
      props: { session: { id: 's1', title: 'Shared', phase: 'Running', accessRole: 'Collaborator' } },
      global: { stubs: { TerminalPane: true } }
    })

    expect(wrapper.text()).not.toContain('Shell')
  })
})
