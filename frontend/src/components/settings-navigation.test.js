// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import SettingsView from './SettingsView.vue'

const mocks = vi.hoisted(() => ({
  api: {},
  auth: { user: 'alice', displayName: 'Alice', email: 'alice@example.com', enabled: true, logout: vi.fn() },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, auth: mocks.auth, config: mocks.config }))

const stubs = {
  AccountDialog: { template: '<div data-pane="account" />' },
  CredentialsDialog: { template: '<div data-pane="credentials" />' },
  SettingsDialog: { props: ['section'], template: '<div :data-pane="section" />' },
  AdminView: { props: ['section'], template: '<div :data-pane="section" />' },
  AdminLimitsView: { template: '<div data-pane="limits" />' },
  McpServersPane: { props: ['mode'], template: '<div :data-pane="mode === \'org\' ? \'org-mcp\' : \'mcp\'" />' },
  SkillsPane: { template: '<div data-pane="skills" />' },
  GroupsPane: { template: '<div data-pane="groups" />' },
  WebhooksPane: { template: '<div data-pane="webhooks" />' }
}
const mountView = (props) => mount(SettingsView, { props, global: { stubs } })
const paneOf = (wrapper) => wrapper.get('[data-pane]').attributes('data-pane')

describe('settings tab navigation', () => {
  beforeEach(() => { mocks.config.gitEnabled = false })

  it('shows the tab given by initialTab', () => {
    expect(paneOf(mountView({ initialTab: 'mcp' }))).toBe('mcp')
  })

  it('follows later changes of initialTab (popstate drives the prop)', async () => {
    const wrapper = mountView({ initialTab: 'mcp' })
    await wrapper.setProps({ initialTab: 'tokens' })
    expect(paneOf(wrapper)).toBe('tokens')
    expect(wrapper.get('[data-settings-tab="tokens"]').classes()).toContain('on')
  })

  it('emits navigate when a subnav tab is clicked', async () => {
    const wrapper = mountView({ initialTab: 'credentials' })
    await wrapper.get('[data-settings-tab="skills"]').trigger('click')
    expect(wrapper.emitted('navigate')).toEqual([['skills']])
    expect(paneOf(wrapper)).toBe('skills')
  })

  it('falls back to the default tab for an admin tab without admin rights, silently', () => {
    const wrapper = mountView({ initialTab: 'users', isAdmin: false })
    expect(paneOf(wrapper)).toBe('credentials')
    expect(wrapper.find('[data-settings-tab="users"]').exists()).toBe(false)
    // No navigate: the URL keeps /settings/users until the admin check has resolved.
    expect(wrapper.emitted('navigate')).toBeUndefined()
  })

  it('shows the admin tab once the admin check resolves', async () => {
    const wrapper = mountView({ initialTab: 'users', isAdmin: false })
    await wrapper.setProps({ isAdmin: true })
    expect(paneOf(wrapper)).toBe('seats')
    expect(wrapper.get('[data-settings-tab="users"]').classes()).toContain('on')
  })

  it('shows the account tab only when a git provider is configured', () => {
    expect(paneOf(mountView({ initialTab: 'account' }))).toBe('credentials')
    mocks.config.gitEnabled = true
    expect(paneOf(mountView({ initialTab: 'account' }))).toBe('account')
  })
})
