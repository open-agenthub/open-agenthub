// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ProjectSidebar from './ProjectSidebar.vue'
import ShareSessionDialog from './ShareSessionDialog.vue'
import SharedSessionView from './SharedSessionView.vue'
import ProjectShareDialog from './ProjectShareDialog.vue'

const apiMocks = vi.hoisted(() => ({
  getSharedSession: vi.fn(),
  listSessionShares: vi.fn(),
  updateShareLink: vi.fn(),
  api: {
    listSessionShares: vi.fn(),
    updateShareLink: vi.fn(),
    createShareUser: vi.fn(), updateShareUser: vi.fn(), deleteShareUser: vi.fn(),
    createShareLink: vi.fn(), deleteShareLink: vi.fn(), updateMcpPolicy: vi.fn(),
    createProject: vi.fn(), updateProject: vi.fn(), deleteProject: vi.fn(),
    listProjectShares: vi.fn(), upsertProjectShare: vi.fn(), deleteProjectShare: vi.fn(), findProjectShareRecipients: vi.fn()
  }
}))

vi.mock('../api.js', () => ({
  api: apiMocks.api,
  getSharedSession: apiMocks.getSharedSession
}))

describe('shared-session UI', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.getSharedSession.mockResolvedValue({ id: 's1', title: 'Shared work', accessRole: 'Viewer' })
    // The overview as the backend spells it (SessionSharingOverview): the policy key is mcpPolicy.
    apiMocks.api.listSessionShares.mockResolvedValue({
      users: [],
      links: [{ id: 'link-1', role: 'Viewer', expiresAt: null }],
      mcpPolicy: { blockedServers: ['filesystem', 'github'], blockedTools: ['mcp__github__create_issue'] }
    })
    apiMocks.api.updateShareLink.mockResolvedValue({})
  })

  it('loads a shared session with the route token', async () => {
    const wrapper = mount(SharedSessionView, {
      props: { token: 'secret-token' },
      global: { stubs: { TerminalView: { props: ['session', 'sharedToken'], template: '<div class="terminal-stub">{{ sharedToken }} · {{ session.title }}</div>' } } }
    })
    await flushPromises()

    expect(apiMocks.getSharedSession).toHaveBeenCalledWith('secret-token')
    expect(wrapper.find('.terminal-stub').text()).toContain('secret-token · Shared work')
  })

  it('updates an existing secret link role and expiration', async () => {
    const wrapper = mount(ShareSessionDialog, { props: { session: { id: 's1', title: 'Owner session' } } })
    await flushPromises()
    await wrapper.get('[data-link-role="link-1"]').setValue('Collaborator')
    await wrapper.get('[data-link-expiration="link-1"]').setValue('2030-01-02T03:04')
    await wrapper.get('[data-save-link="link-1"]').trigger('click')
    await flushPromises()

    expect(apiMocks.api.updateShareLink).toHaveBeenCalledWith('s1', 'link-1', {
      role: 'Collaborator',
      expiresAt: '2030-01-02T03:04'
    })
  })

  it('collapses and reopens a project group', async () => {
    const wrapper = mount(ProjectSidebar, {
      props: {
        projects: [{ id: 'p1', name: 'Core', sortOrder: 0 }],
        sessions: [{ id: 's1', title: 'API', projectId: 'p1' }],
        active: null
      },
      global: { stubs: { SessionList: { props: ['sessions'], template: '<div class="session-stub">{{ sessions.length }}</div>' } } }
    })

    expect(wrapper.find('.session-stub').exists()).toBe(true)
    await wrapper.get('[data-toggle-group="p1"]').trigger('click')
    expect(wrapper.find('.session-stub').exists()).toBe(false)
    await wrapper.get('[data-toggle-group="p1"]').trigger('click')
    expect(wrapper.find('.session-stub').exists()).toBe(true)
  })
})

describe('project sharing (#409)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.api.listProjectShares.mockResolvedValue([
      { subjectType: 'user', subject: 'bob', role: 'Viewer' },
      { subjectType: 'group', subject: 'release-team', role: 'Collaborator' }
    ])
    apiMocks.api.findProjectShareRecipients.mockResolvedValue({ users: [], groups: ['release-team', 'ops'] })
    apiMocks.api.upsertProjectShare.mockResolvedValue({})
    apiMocks.api.deleteProjectShare.mockResolvedValue(null)
  })

  it('opens from the project controls and lists who has access', async () => {
    const wrapper = mount(ProjectSidebar, {
      props: { projects: [{ id: 'p1', name: 'Core', sortOrder: 0 }], sessions: [], active: null },
      global: { stubs: { SessionList: true } }
    })
    await wrapper.get('[data-project-share-open="p1"]').trigger('click')
    await flushPromises()

    expect(apiMocks.api.listProjectShares).toHaveBeenCalledWith('p1')
    expect(wrapper.get('[data-project-share-entry="user:bob"]').text()).toContain('bob')
    expect(wrapper.get('[data-project-share-entry="group:release-team"]').text()).toContain('group')
  })

  it('adds one of the owner\'s groups, changes a role and removes a person', async () => {
    const wrapper = mount(ProjectShareDialog, { props: { project: { id: 'p1', name: 'Core' } } })
    await flushPromises()
    expect(apiMocks.api.findProjectShareRecipients).toHaveBeenCalledWith('p1', '')

    await wrapper.get('[data-project-share-kind]').setValue('group')
    const ops = wrapper.findAll('[aria-label="Your groups"] button').find(button => button.text() === 'ops')
    await ops.trigger('click')
    await wrapper.get('[data-project-share-role]').setValue('Collaborator')
    await wrapper.get('[data-project-share-add]').trigger('click')
    await flushPromises()
    expect(apiMocks.api.upsertProjectShare).toHaveBeenCalledWith('p1', { subjectType: 'group', subject: 'ops', role: 'Collaborator' })

    await wrapper.get('[data-project-share-entry-role="user:bob"]').setValue('Collaborator')
    await flushPromises()
    expect(apiMocks.api.upsertProjectShare).toHaveBeenCalledWith('p1', { subjectType: 'user', subject: 'bob', role: 'Collaborator' })

    await wrapper.get('[data-project-share-entry-role="user:bob"]').setValue('Remove')
    await flushPromises()
    expect(apiMocks.api.deleteProjectShare).toHaveBeenCalledWith('p1', 'user', 'bob')
  })

  it('shows the project\'s shares in the session dialog, read-only', async () => {
    apiMocks.api.listSessionShares.mockResolvedValue({
      users: [], links: [], mcpPolicy: null,
      projectShares: [{ subjectType: 'group', subject: 'release-team', role: 'Viewer' }]
    })
    const wrapper = mount(ShareSessionDialog, { props: { session: { id: 's1', title: 'Owner session' } } })
    await flushPromises()
    const inherited = wrapper.get('[data-share-project-shares]')
    expect(inherited.text()).toContain('release-team')
    expect(inherited.text()).toContain('Viewer · via project')
    expect(inherited.find('select').exists()).toBe(false)
  })
})
