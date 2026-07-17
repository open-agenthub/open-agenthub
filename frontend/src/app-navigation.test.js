// @vitest-environment happy-dom
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import App from './App.vue'

const mocks = vi.hoisted(() => ({
  api: {
    listSessions: vi.fn(), listProjects: vi.fn(), adminAccess: vi.fn(),
    resumeSession: vi.fn(), pauseSession: vi.fn(), deleteSession: vi.fn()
  },
  auth: { enabled: false, isAuthenticated: true, user: 'tester', login: vi.fn(), logout: vi.fn() }
}))

vi.mock('./api.js', () => ({ api: mocks.api, auth: mocks.auth }))

const sessions = [
  { id: 's1', title: 'One', phase: 'Running' },
  { id: 's2', title: 'Two', phase: 'Running' }
]
const stubs = {
  ProjectSidebar: { emits: ['select'], template: '<button data-select @click="$emit(\'select\', \'s1\')">select</button>' },
  TerminalView: { props: ['session'], template: '<div class="terminal-id">{{ session.id }}</div>' },
  SettingsView: { props: ['initialTab'], template: '<div class="settings-tab">{{ initialTab }}</div>' },
  AdminView: true, NewSessionDialog: true, EditSessionDialog: true,
  DuplicateSessionDialog: true, ShareSessionDialog: true, SharedSessionView: true
}

describe('application navigation', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(globalThis, 'setInterval').mockReturnValue(1)
    mocks.api.listSessions.mockResolvedValue(sessions)
    mocks.api.listProjects.mockResolvedValue([])
    mocks.api.adminAccess.mockResolvedValue({ isAdmin: false })
    history.replaceState({}, '', '/')
  })
  afterEach(() => vi.restoreAllMocks())

  it('restores a deep-linked session and follows back-forward navigation', async () => {
    history.replaceState({}, '', '/s/s2')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('.terminal-id').text()).toBe('s2')

    history.pushState({}, '', '/s/s1')
    window.dispatchEvent(new PopStateEvent('popstate'))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.terminal-id').text()).toBe('s1')
  })

  it('writes selected sessions to the deep-link path', async () => {
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    await wrapper.get('[data-select]').trigger('click')
    expect(location.pathname).toBe('/s/s1')
  })

  it('opens the account settings tab after an account callback', async () => {
    history.replaceState({}, '', '/account')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('.settings-tab').text()).toBe('account')
  })
  it('preserves the account callback path during back-forward navigation', async () => {
    history.replaceState({}, '', '/s/s2')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    history.pushState({}, '', '/account')
    window.dispatchEvent(new PopStateEvent('popstate'))
    await wrapper.vm.$nextTick()

    expect(location.pathname).toBe('/account')
    expect(wrapper.get('.settings-tab').text()).toBe('account')
  })

})
