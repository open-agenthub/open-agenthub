// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'
import SessionList from './SessionList.vue'
import SessionsView from './SessionsView.vue'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import { sessionMatches } from '../lib/text.js'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn().mockResolvedValue(''),
    getConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 }),
    listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]),
    sendSessionMessage: vi.fn(),
    decidePermission: vi.fn(),
    createSession: vi.fn(),
    updateSession: vi.fn(),
    getCredentialStatus: vi.fn(),
    getAllowedAgents: vi.fn(),
    mcpServers: vi.fn()
  },
  config: { gitEnabled: false }
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  config: mocks.config,
  getSharedTranscript: vi.fn().mockResolvedValue(''),
  getSharedConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 })
}))

const session = (extra = {}) => ({ id: 's1', title: 'Coder', phase: 'Running', mode: 'Interactive', ...extra })
const message = (extra = {}) => ({
  id: 'm1', from: 'rev-1', fromTitle: 'Code Reviewer', body: 'Please fix MR 42',
  createdAt: '2026-09-20T10:00:00Z', deliveredAt: null, ...extra
})

function mountTerminal(props = {}) {
  return mount(TerminalView, {
    props: { session: session(), ...props },
    global: { stubs: { TerminalPane: true, ShareSessionDialog: true } }
  })
}

// Messages live behind the ✉ button; nothing about them is drawn over the terminal.
async function openPanel(wrapper) {
  await wrapper.get('[data-send-message-toggle]').trigger('click')
  return wrapper.get('[data-send-message]')
}

describe('fleet messages panel in the session view', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listPermissions.mockResolvedValue([])
    mocks.api.listSessionMessages.mockResolvedValue([])
    vi.useFakeTimers()
  })
  afterEach(() => vi.useRealTimers())

  it('shows undelivered messages with sender and body', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([message()])
    const wrapper = mountTerminal()
    await flushPromises()
    await openPanel(wrapper)

    const banner = wrapper.get('[data-agent-message]')
    expect(banner.text()).toContain('Message from agent “Code Reviewer”')
    expect(banner.text()).toContain('Please fix MR 42')
  })

  it('labels messages without a sender session as external', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([message({ from: null, fromTitle: null })])
    const wrapper = mountTerminal()
    await flushPromises()
    await openPanel(wrapper)

    expect(wrapper.get('[data-agent-message]').text()).toContain('Message from outside the fleet')
  })

  it('hides messages the agent already picked up from its inbox', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([
      message(),
      message({ id: 'm2', body: 'old task', deliveredAt: '2026-09-20T10:01:00Z' })
    ])
    const wrapper = mountTerminal()
    await flushPromises()
    await openPanel(wrapper)

    expect(wrapper.findAll('[data-agent-message]')).toHaveLength(1)
    expect(wrapper.text()).not.toContain('old task')
  })

  it('dismisses a message locally without consuming the inbox', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([message()])
    const wrapper = mountTerminal()
    await flushPromises()
    await openPanel(wrapper)

    await wrapper.get('[data-dismiss-message]').trigger('click')

    expect(wrapper.find('[data-agent-message]').exists()).toBe(false)
    // A later poll returning the same undelivered message keeps it dismissed.
    await vi.advanceTimersByTimeAsync(4000)
    await flushPromises()
    expect(wrapper.find('[data-agent-message]').exists()).toBe(false)
  })

  it('draws nothing over the terminal and counts the open messages on the button', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([
      message(),
      message({ id: 'm2', body: 'second' }),
      message({ id: 'm3', body: 'done already', deliveredAt: '2026-09-20T10:01:00Z' })
    ])
    const wrapper = mountTerminal()
    await flushPromises()

    expect(wrapper.find('[data-agent-message]').exists()).toBe(false)
    expect(wrapper.get('[data-message-badge]').text()).toBe('2')

    const panel = await openPanel(wrapper)
    expect(panel.findAll('[data-agent-message]')).toHaveLength(2)
    expect(panel.get('[data-messages-count]').text()).toBe('2 open')

    await panel.findAll('[data-dismiss-message]')[0].trigger('click')
    expect(wrapper.get('[data-message-badge]').text()).toBe('1')
    await panel.findAll('[data-dismiss-message]')[0].trigger('click')
    expect(wrapper.find('[data-message-badge]').exists()).toBe(false)
    expect(panel.get('[data-messages-empty]').exists()).toBe(true)
  })

  it('polls messages alongside permissions but not for shared links or viewers', async () => {
    mountTerminal()
    await flushPromises()
    expect(mocks.api.listSessionMessages).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(4000)
    expect(mocks.api.listSessionMessages).toHaveBeenCalledTimes(2)

    mocks.api.listSessionMessages.mockClear()
    const shared = mountTerminal({ sharedToken: 'tok' })
    await flushPromises()
    expect(mocks.api.listSessionMessages).not.toHaveBeenCalled()
    shared.unmount()

    const viewer = mountTerminal({ session: session({ accessRole: 'Viewer' }) })
    await flushPromises()
    expect(mocks.api.listSessionMessages).not.toHaveBeenCalled()
    viewer.unmount()
  })
})

describe('priority messages in the session view', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listPermissions.mockResolvedValue([])
    mocks.api.listSessionMessages.mockResolvedValue([])
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-20T10:10:00Z'))
  })
  afterEach(() => vi.useRealTimers())

  it('marks an undelivered priority message as waiting in the inbox', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([message({ priority: true })])
    const wrapper = mountTerminal()
    await flushPromises()
    await openPanel(wrapper)

    const banner = wrapper.get('[data-agent-message]')
    expect(banner.attributes('data-priority')).toBe('true')
    expect(banner.text()).toContain('Priority message from agent “Code Reviewer”')
    expect(banner.get('[data-message-delivery]').text()).toBe('waiting in inbox')
  })

  it('keeps a delivered priority message visible for a while and says it reached the agent', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([
      message({ id: 'fresh', priority: true, interrupt: true, deliveredAt: '2026-09-20T10:08:00Z', deliveredVia: 'injected' }),
      message({ id: 'mod', priority: true, deliveredAt: '2026-09-20T10:09:00Z', deliveredVia: 'mod' }),
      message({ id: 'stale', priority: true, deliveredAt: '2026-09-20T09:00:00Z', deliveredVia: 'injected' }),
      message({ id: 'plain', deliveredAt: '2026-09-20T10:09:30Z', deliveredVia: 'inbox' })
    ])
    const wrapper = mountTerminal()
    await flushPromises()
    await openPanel(wrapper)

    const banners = wrapper.findAll('[data-agent-message]')
    expect(banners).toHaveLength(2)
    expect(banners[0].text()).toContain('Interrupt from agent “Code Reviewer”')
    expect(banners[0].get('[data-message-delivery]').text()).toBe('delivered to the agent')
    expect(banners[1].attributes('data-delivered-via')).toBe('mod')
    expect(banners[1].get('[data-message-delivery]').text()).toBe('delivered to the agent')
  })

  it('a plain message has no delivery label and no priority marker', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([message()])
    const wrapper = mountTerminal()
    await flushPromises()
    await openPanel(wrapper)

    const banner = wrapper.get('[data-agent-message]')
    expect(banner.attributes('data-priority')).toBeUndefined()
    expect(banner.find('[data-message-delivery]').exists()).toBe(false)
  })
})

describe('messaging the agent from the session view', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listPermissions.mockResolvedValue([])
    mocks.api.listSessionMessages.mockResolvedValue([])
    mocks.api.sendSessionMessage.mockResolvedValue({ id: 'm-9', to: 's1', deliveredVia: 'injected' })
  })

  it('sends the text with the priority and interrupt flags, interrupt implying priority', async () => {
    const wrapper = mountTerminal()
    await flushPromises()
    expect(wrapper.find('[data-send-message]').exists()).toBe(false)

    await wrapper.get('[data-send-message-toggle]').trigger('click')
    const card = wrapper.get('[data-send-message]')
    await card.get('[data-send-message-priority]').setValue(false)
    await card.get('[data-send-message-interrupt]').setValue(true)
    expect(card.get('[data-send-message-priority]').element.checked).toBe(true)
    await card.get('[data-send-message-text]').setValue('  Stop and look at MR 42  ')
    await card.get('[data-send-message-submit]').trigger('click')
    await flushPromises()

    expect(mocks.api.sendSessionMessage).toHaveBeenCalledWith('s1', {
      body: 'Stop and look at MR 42', priority: true, interrupt: true
    })
    expect(wrapper.get('[data-send-message-note]').text()).toBe('Delivered to the agent.')
    expect(wrapper.get('[data-send-message-text]').element.value).toBe('')
    // The banner list is refreshed so the pushed message shows up at once.
    expect(mocks.api.listSessionMessages).toHaveBeenCalledTimes(2)
  })

  it('tells the person when the message only reached the inbox', async () => {
    mocks.api.sendSessionMessage.mockResolvedValue({ id: 'm-9', to: 's1', deliveredVia: 'inbox', reason: 'non_interactive' })
    const wrapper = mountTerminal()
    await flushPromises()

    await wrapper.get('[data-send-message-toggle]').trigger('click')
    await wrapper.get('[data-send-message-text]').setValue('later please')
    await wrapper.get('[data-send-message-submit]').trigger('click')
    await flushPromises()

    expect(mocks.api.sendSessionMessage).toHaveBeenCalledWith('s1', { body: 'later please', priority: true, interrupt: false })
    expect(wrapper.get('[data-send-message-note]').text()).toContain('Waiting in the inbox')
  })

  it('offers the panel to managers only, and the send form only while the session is live', async () => {
    const stopped = mountTerminal({ session: session({ phase: 'Succeeded' }) })
    await flushPromises()
    // A finished session still has an inbox worth reading, but no pod to push a message into.
    const panel = await openPanel(stopped)
    expect(panel.find('[data-send-message-form]').exists()).toBe(false)
    expect(panel.get('[data-messages-empty]').exists()).toBe(true)
    stopped.unmount()

    const shared = mountTerminal({ sharedToken: 'tok' })
    await flushPromises()
    expect(shared.find('[data-send-message-toggle]').exists()).toBe(false)
    shared.unmount()

    const viewer = mountTerminal({ session: session({ accessRole: 'Viewer' }) })
    await flushPromises()
    expect(viewer.find('[data-send-message-toggle]').exists()).toBe(false)
  })
})

describe('agent descriptions in the session lists', () => {
  it('sidebar list shows the description line only when set', () => {
    const wrapper = mount(SessionList, {
      props: {
        sessions: [
          session({ description: 'Implements tasks from the reviewer' }),
          session({ id: 's2', title: 'Bare', description: null })
        ]
      }
    })

    const descriptions = wrapper.findAll('[data-session-description]')
    expect(descriptions).toHaveLength(1)
    expect(descriptions[0].text()).toBe('Implements tasks from the reviewer')
  })

  it('sessions page rows show the description under the title', () => {
    const wrapper = mount(SessionsView, {
      props: {
        sessions: [session({ projectId: 'p1', description: 'Watches the issue tracker' })],
        projects: [{ id: 'p1', name: 'Fleet', sortOrder: 0 }]
      }
    })

    expect(wrapper.get('[data-session-description]').text()).toBe('Watches the issue tracker')
  })

  it('free-text search matches the description', () => {
    const agent = session({ description: 'Reviews merge requests' })
    expect(sessionMatches(agent, 'merge requests')).toBe(true)
    expect(sessionMatches(agent, 'nonsense')).toBe(false)
  })
})

describe('agent description in the session dialogs', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({})
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude'] })
    mocks.api.mcpServers.mockResolvedValue([])
    mocks.api.createSession.mockResolvedValue({ id: 'new' })
    mocks.api.updateSession.mockResolvedValue({ id: 's1' })
  })

  const stubs = { global: { stubs: { RepoPicker: { template: '<div />' } } } }

  it('sends the description when creating a session', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...stubs })
    await flushPromises()
    await wrapper.get('[data-description]').setValue('  Reviews merge requests ')
    await wrapper.get('[data-submit]').trigger('click')

    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      description: 'Reviews merge requests'
    }))
  })

  it('omits an empty description on create and clears it on edit', async () => {
    const create = mount(NewSessionDialog, { props: { projects: [] }, ...stubs })
    await flushPromises()
    await create.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0].description).toBeNull()

    const edit = mount(EditSessionDialog, {
      props: {
        projects: [],
        session: {
          id: 's1', title: 'Coder', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription',
          description: 'stale text', policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] },
          repos: [], cpu: '500m', memory: '1Gi'
        }
      },
      ...stubs
    })
    await flushPromises()
    expect(edit.get('[data-description]').element.value).toBe('stale text')
    await edit.get('[data-description]').setValue('')
    await edit.get('[data-submit]').trigger('click')

    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ description: '' }))
  })
})
