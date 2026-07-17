// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ProjectSidebar from './ProjectSidebar.vue'
import ShareSessionDialog from './ShareSessionDialog.vue'
import SharedSessionView from './SharedSessionView.vue'

const apiMocks = vi.hoisted(() => ({
  getSharedSession: vi.fn(),
  listSessionShares: vi.fn(),
  updateShareLink: vi.fn(),
  api: {
    listSessionShares: vi.fn(),
    updateShareLink: vi.fn(),
    createShareUser: vi.fn(), updateShareUser: vi.fn(), deleteShareUser: vi.fn(),
    createShareLink: vi.fn(), deleteShareLink: vi.fn(), updateMcpPolicy: vi.fn(),
    createProject: vi.fn(), updateProject: vi.fn(), deleteProject: vi.fn()
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
    apiMocks.api.listSessionShares.mockResolvedValue({
      users: [],
      links: [{ id: 'link-1', role: 'Viewer', expiresAt: null }],
      policy: { blockedServers: [], blockedTools: [] }
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
