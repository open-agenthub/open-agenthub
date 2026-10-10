// @vitest-environment happy-dom
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import App from './App.vue'
import LicenseGate from './components/LicenseGate.vue'

const mocks = vi.hoisted(() => ({
  api: {
    listSessions: vi.fn(), listProjects: vi.fn(), adminAccess: vi.fn(),
    resumeSession: vi.fn(), pauseSession: vi.fn(), deleteSession: vi.fn()
  },
  auth: { enabled: false, isAuthenticated: true, user: 'tester', login: vi.fn(), logout: vi.fn() },
  config: { gitEnabled: false, version: 'dev', repoUrl: '' }
}))

vi.mock('./api.js', () => ({ api: mocks.api, auth: mocks.auth, config: mocks.config }))

const sessions = [
  { id: 's1', title: 'One', phase: 'Running' },
  { id: 's2', title: 'Two', phase: 'Running' }
]
const stubs = {
  ProjectSidebar: { emits: ['select'], template: '<button data-select @click="$emit(\'select\', \'s1\')">select</button>' },
  TerminalView: { props: ['session'], template: '<div class="terminal-id">{{ session.id }}</div>' },
  SettingsView: {
    props: ['initialTab'], emits: ['navigate', 'close'],
    template: '<div><div class="settings-tab">{{ initialTab }}</div><button data-go-skills @click="$emit(\'navigate\', \'skills\')">skills</button><button data-close-settings @click="$emit(\'close\')">close</button></div>'
  },
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

  it('opens the account settings tab after an account callback and canonicalises the alias', async () => {
    history.replaceState({}, '', '/account?git=connected')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('.settings-tab').text()).toBe('account')
    // Rewritten in place (no extra history entry) and the OAuth marker survives for AccountDialog.
    expect(location.pathname).toBe('/settings/account')
    expect(location.search).toBe('?git=connected')
  })

  it('restores the account tab when navigating back to the alias', async () => {
    history.replaceState({}, '', '/s/s2')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    history.pushState({}, '', '/account')
    window.dispatchEvent(new PopStateEvent('popstate'))
    await wrapper.vm.$nextTick()

    expect(location.pathname).toBe('/settings/account')
    expect(wrapper.get('.settings-tab').text()).toBe('account')
  })

  it('opens the tab named in the settings path on reload', async () => {
    history.replaceState({}, '', '/settings/mcp')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('.settings-tab').text()).toBe('mcp')
    expect(location.pathname).toBe('/settings/mcp')
    expect(wrapper.find('.terminal-id').exists()).toBe(false)
  })

  it('falls back to the default tab for an unknown settings path and corrects the URL', async () => {
    history.replaceState({}, '', '/settings/does-not-exist')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('.settings-tab').text()).toBe('credentials')
    expect(location.pathname).toBe('/settings/credentials')
  })

  it('opens the default tab for the bare settings path', async () => {
    history.replaceState({}, '', '/settings')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('.settings-tab').text()).toBe('credentials')
    expect(location.pathname).toBe('/settings/credentials')
  })

  it('writes the settings path when the settings are opened and the tab changes', async () => {
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    await wrapper.get('button[title="Settings"]').trigger('click')
    expect(location.pathname).toBe('/settings/credentials')

    await wrapper.get('[data-go-skills]').trigger('click')
    expect(location.pathname).toBe('/settings/skills')
    expect(wrapper.get('.settings-tab').text()).toBe('skills')
  })

  it('switches the tab on popstate between two settings paths', async () => {
    history.replaceState({}, '', '/settings/mcp')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    await wrapper.get('[data-go-skills]').trigger('click')
    expect(location.pathname).toBe('/settings/skills')

    // Simulate the back button: the browser restores the previous URL, then fires popstate.
    history.replaceState({}, '', '/settings/mcp')
    window.dispatchEvent(new PopStateEvent('popstate'))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.settings-tab').text()).toBe('mcp')
    expect(location.pathname).toBe('/settings/mcp')
  })

  it('returns to the open session when the settings are closed', async () => {
    history.replaceState({}, '', '/s/s1')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('.terminal-id').text()).toBe('s1')

    await wrapper.get('button[title="Settings"]').trigger('click')
    expect(location.pathname).toBe('/settings/credentials')
    expect(wrapper.find('.terminal-id').exists()).toBe(false)

    await wrapper.get('[data-close-settings]').trigger('click')
    expect(location.pathname).toBe('/s/s1')
    expect(wrapper.get('.terminal-id').text()).toBe('s1')
  })

  it('goes home when the settings are closed without a session', async () => {
    history.replaceState({}, '', '/settings/tokens')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    await wrapper.get('[data-close-settings]').trigger('click')
    expect(location.pathname).toBe('/')
  })

  it('selecting a session from the settings writes the session path', async () => {
    history.replaceState({}, '', '/settings/mcp')
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    await wrapper.get('[data-select]').trigger('click')
    expect(location.pathname).toBe('/s/s1')
    expect(wrapper.get('.terminal-id').text()).toBe('s1')
  })
})

describe('license gate wiring', () => {
  // A gate deep inside the session page (the share dialog lives in TerminalView) gets the admin
  // flag and the settings navigation from App.vue by injection, not by props.
  const gateStubs = {
    ...stubs,
    TerminalView: { components: { LicenseGate }, template: '<LicenseGate feature="Sharing sessions with other users" />' }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(globalThis, 'setInterval').mockReturnValue(1)
    mocks.api.listSessions.mockResolvedValue(sessions)
    mocks.api.listProjects.mockResolvedValue([])
    history.replaceState({}, '', '/s/s1')
  })
  afterEach(() => vi.restoreAllMocks())

  it('shows an admin the link and opens the license tab in place', async () => {
    mocks.api.adminAccess.mockResolvedValue({ isAdmin: true })
    const wrapper = mount(App, { global: { stubs: gateStubs } })
    await flushPromises()

    await wrapper.get('[data-license-link]').trigger('click')
    expect(location.pathname).toBe('/settings/license')
    expect(wrapper.get('.settings-tab').text()).toBe('license')
    expect(wrapper.find('[data-license-link]').exists()).toBe(false) // the session page is gone
  })

  it('shows a non-admin the hint instead of a link', async () => {
    mocks.api.adminAccess.mockResolvedValue({ isAdmin: false })
    const wrapper = mount(App, { global: { stubs: gateStubs } })
    await flushPromises()

    expect(wrapper.find('[data-license-link]').exists()).toBe(false)
    expect(wrapper.get('[data-license-hint]').text()).toContain('Ask an administrator')
    expect(location.pathname).toBe('/s/s1')
  })
})

describe('sidebar footer', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(globalThis, 'setInterval').mockReturnValue(1)
    mocks.api.listSessions.mockResolvedValue(sessions)
    mocks.api.listProjects.mockResolvedValue([])
    mocks.api.adminAccess.mockResolvedValue({ isAdmin: false })
    mocks.config.version = 'dev'
    mocks.config.repoUrl = ''
    history.replaceState({}, '', '/')
  })
  afterEach(() => vi.restoreAllMocks())

  it('shows the deployed version and links to the repository in a new tab', async () => {
    mocks.config.version = '0.12.0'
    mocks.config.repoUrl = 'https://github.com/open-agenthub/open-agenthub'
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    const foot = wrapper.get('[data-app-version]')
    expect(foot.text()).toBe('Open AgentHub v0.12.0 · GitHub ↗')
    const link = foot.get('[data-repo-link]')
    expect(link.attributes('href')).toBe('https://github.com/open-agenthub/open-agenthub')
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')).toBe('noopener')
  })

  it('labels an untagged build "dev" and still links the repository', async () => {
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()

    const foot = wrapper.get('[data-app-version]')
    expect(foot.text()).toBe('Open AgentHub dev · GitHub ↗')
    // The backend sent no repoUrl (older backend), so the bundled constant is the fallback.
    expect(foot.get('[data-repo-link]').attributes('href')).toBe('https://github.com/open-agenthub/open-agenthub')
  })

  it('shows a branch build by its commit rather than inventing a release number', async () => {
    mocks.config.version = 'a1b2c3d'
    const wrapper = mount(App, { global: { stubs } })
    await flushPromises()
    expect(wrapper.get('[data-app-version]').text()).toBe('Open AgentHub a1b2c3d · GitHub ↗')
  })
})
