// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ChatPane from './ChatPane.vue'

const mocks = vi.hoisted(() => ({ sockets: [], transcript: '', uploadError: null }))

vi.mock('../api.js', () => ({
  api: {
    getTranscript: vi.fn().mockImplementation(() => Promise.resolve(mocks.transcript)),
    reserveSessionFile: vi.fn().mockResolvedValue({ file: { id: 'file-1' }, upload: { kind: 'proxy', url: '/upload' } }),
    uploadSessionFile: vi.fn().mockImplementation(() => mocks.uploadError ? Promise.reject(mocks.uploadError) : Promise.resolve()),
    completeSessionFile: vi.fn().mockResolvedValue({ id: 'file-1', name: 'shot.png', mimeType: 'image/png', size: 12, state: 'Ready' }),
    deleteSessionFile: vi.fn().mockResolvedValue(null)
  },
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
    mocks.uploadError = null
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
    const sent = mocks.sockets[0].sent[0]
    expect(sent).toMatchObject({ type: 'chat', text: 'hello agent' })
    expect(sent.clientTurnId).toBeTypeOf('string')
    expect(wrapper.find('[data-chat-input]').element.value).toBe('hello agent')
    mocks.sockets[0].onmessage({ data: line({ type: 'agenthub', subtype: 'chat_delivered', clientTurnId: sent.clientTurnId }) })
    await flushPromises()
    expect(wrapper.find('[data-chat-input]').element.value).toBe('')
  })

  it('pastes an image, uploads it, and sends its ready file id', async () => {
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Running' } } })
    await flushPromises()
    const file = new File([new Uint8Array(12)], 'shot.png', { type: 'image/png', lastModified: 1 })
    await wrapper.get('[data-chat-input]').trigger('paste', { clipboardData: { files: [file] } })
    await flushPromises()
    expect(wrapper.get('[data-attachment]').text()).toContain('shot.png')
    await wrapper.get('[data-chat-send]').trigger('click')
    const sent = mocks.sockets[0].sent.at(-1)
    expect(sent).toMatchObject({ type: 'chat', text: '', attachments: ['file-1'] })
    expect(wrapper.find('[data-attachment]').exists()).toBe(true)
    const later = new File([new Uint8Array(12)], 'later.png', { type: 'image/png', lastModified: 2 })
    await wrapper.get('[data-chat-input]').trigger('paste', { clipboardData: { files: [later] } })
    await flushPromises()
    expect(wrapper.findAll('[data-attachment]')).toHaveLength(1)
    mocks.sockets[0].onmessage({ data: line({ type: 'agenthub', subtype: 'error',
      code: 'attachment_delivery_failed', clientTurnId: sent.clientTurnId }) })
    await flushPromises()
    expect(wrapper.find('[data-attachment]').exists()).toBe(true)
    await wrapper.get('[data-chat-send]').trigger('click')
    const retried = mocks.sockets[0].sent.at(-1)
    expect(retried.clientTurnId).not.toBe(sent.clientTurnId)
    mocks.sockets[0].onmessage({ data: line({ type: 'agenthub', subtype: 'chat_delivered', clientTurnId: retried.clientTurnId }) })
    await flushPromises()
    expect(wrapper.find('[data-attachment]').exists()).toBe(false)
  })

  it('keeps the draft and gates send when an upload fails', async () => {
    mocks.uploadError = Object.assign(new Error('failed'), { code: 'content_type_mismatch' })
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Running' } } })
    await flushPromises()
    await wrapper.get('[data-chat-input]').setValue('keep me')
    const file = new File([new Uint8Array(12)], 'bad.png', { type: 'image/png', lastModified: 1 })
    await wrapper.get('[data-chat-input]').trigger('paste', { clipboardData: { files: [file] } })
    await flushPromises()
    expect(wrapper.get('[data-chat-send]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-attachment-error]').text()).toContain('File content does not match its type')
    expect(wrapper.get('[data-chat-input]').element.value).toBe('keep me')
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

  it('follows streamed output only while the reader is at the bottom', async () => {
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Running' } } })
    await flushPromises()
    const socket = mocks.sockets[0]
    socket.onopen()
    const el = wrapper.get('[data-chat-scroll]').element
    let contentHeight = 2000
    Object.defineProperty(el, 'scrollHeight', { configurable: true, get: () => contentHeight })
    Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => 600 })
    const delta = text => socket.onmessage({ data: line({ type: 'assistant', message: { id: 'm1', content: [{ type: 'text', text }] } }) })

    delta('first')
    await flushPromises()
    expect(el.scrollTop).toBe(2000)

    // Reader scrolls up to read earlier output; further streaming must leave the view alone.
    el.scrollTop = 300
    await wrapper.get('[data-chat-scroll]').trigger('scroll')
    for (const text of ['second', 'third', 'fourth']) {
      contentHeight += 400
      delta(text)
      await flushPromises()
      expect(el.scrollTop).toBe(300)
    }

    // Back near the bottom (inside the threshold): following resumes.
    el.scrollTop = contentHeight - 600 - 20
    await wrapper.get('[data-chat-scroll]').trigger('scroll')
    contentHeight += 400
    delta('fifth')
    await flushPromises()
    expect(el.scrollTop).toBe(contentHeight)

    // Sending from a scrolled-up position re-pins, so the reply is visible.
    el.scrollTop = 0
    await wrapper.get('[data-chat-scroll]').trigger('scroll')
    await wrapper.get('[data-chat-input]').setValue('next')
    await wrapper.get('[data-chat-send]').trigger('click')
    await flushPromises()
    expect(el.scrollTop).toBe(contentHeight)
  })

  it('hides the composer for read-only viewers', async () => {
    const wrapper = mount(ChatPane, { props: { session: { id: 's1', phase: 'Running' }, readonly: true } })
    await flushPromises()
    expect(wrapper.find('[data-chat-input]').exists()).toBe(false)
  })
  it('keeps shared writable chat text while withholding authenticated upload controls', async () => {
    const wrapper = mount(ChatPane, { props: {
      session: { id: 's1', phase: 'Running' }, sharedToken: 'share-token'
    } })
    await flushPromises()
    expect(wrapper.find('[data-chat-attach]').exists()).toBe(false)
    await wrapper.get('[data-chat-input]').setValue('shared message')
    await wrapper.get('[data-chat-send]').trigger('click')
    expect(mocks.sockets[0].sent[0]).toMatchObject({ type: 'chat', text: 'shared message' })
  })
})
