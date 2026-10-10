// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import SettingsView from './SettingsView.vue'

const mocks = vi.hoisted(() => ({
  api: { deleteAccount: vi.fn() },
  auth: { user: 'alice', displayName: 'Alice', email: 'alice@example.com', enabled: true, logout: vi.fn() },
  config: { gitEnabled: false, version: 'dev', repoUrl: '' }
}))
vi.mock('../api.js', () => ({ api: mocks.api, auth: mocks.auth, config: mocks.config }))

const stubs = {
  AccountDialog: true, CredentialsDialog: true, SettingsDialog: true, AdminView: true,
  AdminLimitsView: true, McpServersPane: true, SkillsPane: true, GroupsPane: true, WebhooksPane: true
}
const mountProfile = () => mount(SettingsView, { props: { initialTab: 'profile' }, global: { stubs } })

describe('profile about line', () => {
  beforeEach(() => {
    mocks.config.version = 'dev'
    mocks.config.repoUrl = ''
  })

  it('names the deployed version and links the repository and the docs', () => {
    mocks.config.version = '0.12.0'
    mocks.config.repoUrl = 'https://github.com/open-agenthub/open-agenthub'
    const wrapper = mountProfile()

    expect(wrapper.get('[data-about-version]').text()).toBe('Open AgentHub v0.12.0')
    const repo = wrapper.get('[data-about-repo]')
    expect(repo.attributes('href')).toBe('https://github.com/open-agenthub/open-agenthub')
    expect(repo.attributes('target')).toBe('_blank')
    expect(repo.attributes('rel')).toBe('noopener')
    expect(wrapper.get('[data-about-docs]').attributes('href')).toBe('https://open-agenthub.github.io/docs')
  })

  it('says dev for a local build and falls back to the bundled repository link', () => {
    const wrapper = mountProfile()
    expect(wrapper.get('[data-about-version]').text()).toBe('Open AgentHub dev')
    expect(wrapper.get('[data-about-repo]').attributes('href')).toBe('https://github.com/open-agenthub/open-agenthub')
  })
})
