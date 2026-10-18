// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, onMounted } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from '../TerminalView.vue'
import { workspaceDrafts, workspaceSent } from '../../lib/session-drafts.js'

// #153: sent and fleet messages the agent has not taken yet are shown below the conversation.
const mocks = vi.hoisted(() => ({
  api: { getConversation: vi.fn(), listPermissions: vi.fn().mockResolvedValue([]), listSessionMessages: vi.fn().mockResolvedValue([]) },
  send: vi.fn()
}))
vi.mock('../../api.js', () => ({ api: mocks.api, getSharedConversation: vi.fn() }))
vi.mock('../../lib/mermaid.js', () => ({ renderMermaidBlocks: vi.fn() }))

const session = { id: 's1', agent: 'Claude', title: 'Build', phase: 'Running', mode: 'Interactive', accessRole: 'Owner' }
const page = entries => ({ source: 'native', entries, text: '', offset: 0, nextOffset: entries.length, length: entries.length })
const wrappers = []
function view() {
  const wrapper = mount(TerminalView, {
    props: { session, initialUi: 'workspace' },
    global: { stubs: {
      SessionWorkspace: { template: '<div><slot /></div>' }, ShareSessionDialog: true,
      TerminalPane: defineComponent({
        emits: ['status'],
        setup(_props, { emit, expose }) {
          expose({ submitMessage: mocks.send, interruptAgent: vi.fn() })
          onMounted(() => emit('status', 'connected'))
          return () => null
        }
      })
    } }
  })
  wrappers.push(wrapper)
  return wrapper
}
const poll = () => new Promise(resolve => setTimeout(resolve, 1100))
async function send(wrapper, text) {
  await wrapper.get('[data-workspace-composer] textarea').setValue(text)
  await wrapper.get('[data-workspace-composer]').trigger('submit')
  await flushPromises()
}
beforeEach(() => {
  workspaceDrafts.clear()
  workspaceSent.clear()
  vi.clearAllMocks()
  mocks.send.mockResolvedValue(undefined)
  mocks.api.listSessionMessages.mockResolvedValue([])
  mocks.api.getConversation.mockResolvedValue(page([{ role: 'user', text: 'Build it' }, { role: 'assistant', text: 'Working on it' }]))
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

describe('pending queue below the conversation', () => {
  it('shows a sent message as pending until the CLI takes it, then only in the conversation', async () => {
    const wrapper = view()
    await flushPromises()
    await send(wrapper, 'Also update the docs')
    const pending = wrapper.get('[data-pending-message]')
    expect(pending.attributes('data-pending-kind')).toBe('sent')
    expect(pending.text()).toContain('Also update the docs')
    expect(pending.text()).toContain('waiting for the agent to take it')

    mocks.api.getConversation.mockResolvedValue(page([{ role: 'user', text: 'Build it' }, { role: 'assistant', text: 'Working on it' },
      { role: 'user', text: 'Also update the docs' }]))
    await poll()
    await flushPromises()
    expect(wrapper.find('[data-pending-message]').exists()).toBe(false)
    expect(wrapper.findAll('[data-workspace-role=user]').some(el => el.text().includes('Also update the docs'))).toBe(true)
  })

  it('does not follow a slash command, which no transcript records', async () => {
    const wrapper = view()
    await flushPromises()
    await send(wrapper, '/model')
    expect(wrapper.find('[data-pending-message]').exists()).toBe(false)
  })

  it('a pending entry can be dismissed, and is kept per session across remounts', async () => {
    const first = view()
    await flushPromises()
    await send(first, 'Keep me')
    first.unmount()
    const second = view()
    await flushPromises()
    expect(second.get('[data-pending-message]').text()).toContain('Keep me')
    await second.get('[data-pending-dismiss]').trigger('click')
    expect(second.find('[data-pending-message]').exists()).toBe(false)
  })

  it('shows an undelivered fleet message as pending and a delivered one as a received message (#186)', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([
      { id: 'm1', from: 'abc123', fromTitle: 'Code Reviewer', body: 'Look at MR 42', createdAt: '2026-10-18T10:00:00Z', deliveredAt: null, priority: false, interrupt: false, deliveredVia: null }
    ])
    const wrapper = view()
    await flushPromises()
    const pending = wrapper.get('[data-pending-message]')
    expect(pending.attributes('data-pending-kind')).toBe('fleet')
    expect(pending.text()).toContain('Message from agent “Code Reviewer”')
    expect(pending.text()).toContain('waiting in the agent’s inbox')

    mocks.api.listSessionMessages.mockResolvedValue([
      { id: 'm1', from: 'abc123', fromTitle: 'Code Reviewer', body: 'Look at MR 42', createdAt: '2026-10-18T10:00:00Z', deliveredAt: '2026-10-18T10:00:05Z', priority: false, interrupt: false, deliveredVia: 'mod' }
    ])
    mocks.api.getConversation.mockResolvedValue(page([{ role: 'user', text: 'Build it' },
      { role: 'user', text: '[AgentHub message from agent "Code Reviewer" (abc123)]\nLook at MR 42' }]))
    await new Promise(resolve => setTimeout(resolve, 4100))
    await flushPromises()
    expect(wrapper.find('[data-pending-message]').exists()).toBe(false)
    const received = wrapper.get('[data-workspace-role=fleet]')
    expect(received.text()).toContain('Message from agent “Code Reviewer”')
    expect(received.text()).toContain('Look at MR 42')
  }, 10000)
})
