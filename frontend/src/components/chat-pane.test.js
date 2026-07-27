// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ChatPane from './ChatPane.vue'

const mocks = vi.hoisted(() => ({ sockets: [], transcript: '' }))

vi.mock('../api.js', () => ({
  api: { getTranscript: vi.fn().mockImplementation(() => Promise.resolve(mocks.transcript)) },
  terminalUrl: vi.fn().mockResolvedValue('ws://terminal'),
  sharedTerminalUrl: vi.fn().mockReturnValue('ws://shared'),
  getSharedTranscript: vi.fn().mockImplementation(() => Promise.resolve(mocks.transcript))
}))

class MockSocket {
  static OPEN = 1
  constructor(url) { this.url = url; this.readyState = MockSocket.OPEN; this.sent = []; mocks.sockets.push(this) }
  send(value) { this.sent.push(JSON.parse(value)) }
  close() {}
}

const line = value => JSON.stringify(value) + '\n'

describe('ChatPane', () => {
  beforeEach(() => {
    mocks.sockets.length = 0
    mocks.transcript = ''
    globalThis.WebSocket = MockSocket
  })

  it('replays a finished session from the transcript as chat items', async () => {
    mocks.transcript =
      line({ type: 'user', agenthub_echo: true, message: { content: [{ type: 'text', text: 'fix the bug' }] } }) +
      line({ type: 'assistant', message: { id: 'm1', content: [{ type: 'text', text: 'Done — see `app.js`.' }] } }) +
      line({ type: 'agenthub', subtype: 'exit', code: 0, signal: null })
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Succeeded' } } })
    await flushPromises()
    expect(wrapper.find('[data-chat-user]').text()).toContain('fix the bug')
    expect(wrapper.find('[data-chat-assistant]').html()).toContain('<code>app.js</code>')
    expect(wrapper.text()).toContain('Session ended (code 0).')
    expect(wrapper.find('[data-chat-input]').exists()).toBe(false)
    expect(mocks.sockets).toHaveLength(0)
  })

  it('sends composer input as a chat message over the socket', async () => {
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Running' } } })
    await flushPromises()
    mocks.sockets[0].onopen()
    await wrapper.find('[data-chat-input]').setValue('hello agent')
    await wrapper.find('[data-chat-send]').trigger('click')
    expect(mocks.sockets[0].sent).toEqual([{ type: 'chat', text: 'hello agent' }])
    expect(wrapper.find('[data-chat-input]').element.value).toBe('')
  })

  it('renders streamed events and offers Stop while the agent works', async () => {
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Running' } } })
    await flushPromises()
    const socket = mocks.sockets[0]
    socket.onopen()
    socket.onmessage({ data: line({ type: 'user', agenthub_echo: true, message: { content: [{ type: 'text', text: 'go' }] } }) })
    await flushPromises()
    expect(wrapper.find('[data-chat-stop]').exists()).toBe(true)
    await wrapper.find('[data-chat-stop]').trigger('click')
    expect(socket.sent).toEqual([{ type: 'interrupt' }])
    socket.onmessage({ data: line({ type: 'result', is_error: false }) })
    await flushPromises()
    expect(wrapper.find('[data-chat-stop]').exists()).toBe(false)
  })

  it('hides the composer for read-only viewers', async () => {
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Running' }, readonly: true } })
    await flushPromises()
    expect(wrapper.find('[data-chat-input]').exists()).toBe(false)
  })
})
