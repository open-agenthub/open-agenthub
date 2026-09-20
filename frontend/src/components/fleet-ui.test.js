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
    listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]),
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
  getSharedTranscript: vi.fn().mockResolvedValue('')
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

describe('fleet message banner in the session view', () => {
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

    const banner = wrapper.get('[data-agent-message]')
    expect(banner.text()).toContain('Message from agent “Code Reviewer”')
    expect(banner.text()).toContain('Please fix MR 42')
  })

  it('labels messages without a sender session as external', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([message({ from: null, fromTitle: null })])
    const wrapper = mountTerminal()
    await flushPromises()

    expect(wrapper.get('[data-agent-message]').text()).toContain('Message from outside the fleet')
  })

  it('hides messages the agent already picked up from its inbox', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([
      message(),
      message({ id: 'm2', body: 'old task', deliveredAt: '2026-09-20T10:01:00Z' })
    ])
    const wrapper = mountTerminal()
    await flushPromises()

    expect(wrapper.findAll('[data-agent-message]')).toHaveLength(1)
    expect(wrapper.text()).not.toContain('old task')
  })

  it('dismisses a message locally without consuming the inbox', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([message()])
    const wrapper = mountTerminal()
    await flushPromises()

    await wrapper.get('[data-dismiss-message]').trigger('click')

    expect(wrapper.find('[data-agent-message]').exists()).toBe(false)
    // A later poll returning the same undelivered message keeps it dismissed.
    await vi.advanceTimersByTimeAsync(4000)
    await flushPromises()
    expect(wrapper.find('[data-agent-message]').exists()).toBe(false)
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
