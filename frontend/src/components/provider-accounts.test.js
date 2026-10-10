// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import DuplicateSessionDialog from './DuplicateSessionDialog.vue'
import ProviderAccountsPane from './ProviderAccountsPane.vue'
import CredentialsDialog from './CredentialsDialog.vue'
import TerminalView from './TerminalView.vue'
import { accountOptionLabel, defaultAccountId } from '../lib/agent.js'

const mocks = vi.hoisted(() => ({
  api: {
    createSession: vi.fn(), updateSession: vi.fn(), duplicateSession: vi.fn(),
    getCredentialStatus: vi.fn(), storeCredentials: vi.fn(), deleteSubscriptionCredential: vi.fn(),
    getAllowedAgents: vi.fn(), mcpServers: vi.fn(),
    listProviderAccounts: vi.fn(), updateProviderAccount: vi.fn(), deleteProviderAccount: vi.fn(),
    switchSessionCredential: vi.fn(),
    getTranscript: vi.fn().mockResolvedValue(''), listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]), decidePermission: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config, getSharedTranscript: vi.fn().mockResolvedValue('') }))

const work = { id: 'work0001', label: 'Work', email: 'me@example.com', organization: 'Example Org', isDefault: true, createdAt: '2026-10-01T00:00:00Z' }
const personal = { id: 'home0002', label: 'Personal', email: 'me@home.example', organization: null, isDefault: false, createdAt: '2026-10-02T00:00:00Z' }
const twoClaude = () => ({ Claude: [work, personal], Codex: [], Cursor: [], OpenClaw: [] })
const mountOptions = { global: { stubs: { RepoPicker: { template: '<div data-repo-picker></div>' } } } }

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.getCredentialStatus.mockResolvedValue({ claudeSubscription: true })
  mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw'] })
  mocks.api.mcpServers.mockResolvedValue([])
  mocks.api.listProviderAccounts.mockResolvedValue(twoClaude())
  mocks.api.createSession.mockResolvedValue({ id: 'new' })
  mocks.api.updateSession.mockResolvedValue({ id: 's1' })
  mocks.api.duplicateSession.mockResolvedValue({ id: 'copy' })
  mocks.api.updateProviderAccount.mockResolvedValue(null)
  mocks.api.deleteProviderAccount.mockResolvedValue(null)
  mocks.api.deleteSubscriptionCredential.mockResolvedValue(null)
  mocks.api.switchSessionCredential.mockResolvedValue({ id: 's1' })
})

describe('account helpers', () => {
  it('pick the default account and describe one with its identity', () => {
    expect(defaultAccountId([personal, work])).toBe('work0001')
    expect(defaultAccountId([{ id: 'only' }])).toBe('only')
    expect(defaultAccountId([])).toBe('')
    expect(accountOptionLabel(work)).toBe('Work — me@example.com · Example Org')
    expect(accountOptionLabel({ id: 'x', label: 'Plain' })).toBe('Plain')
  })
})

describe('account choice when creating, editing and duplicating a session', () => {
  it('shows the dropdown only from two accounts of the chosen agent and preselects the default', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    const select = wrapper.get('[data-account-select]')
    expect(select.element.value).toBe('work0001')
    expect(wrapper.findAll('[data-account-option]').map(o => o.text())).toEqual([
      'Work — me@example.com · Example Org (default)', 'Personal — me@home.example'
    ])

    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    expect(wrapper.find('[data-account-select]').exists()).toBe(false)
  })

  it('stays hidden with a single login, so the session follows the default', async () => {
    mocks.api.listProviderAccounts.mockResolvedValue({ Claude: [work] })
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.find('[data-account-select]').exists()).toBe(false)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0].credentialId).toBeNull()
  })

  it('is hidden for API-key billing and tolerates a backend without the listing', async () => {
    mocks.api.listProviderAccounts.mockRejectedValue(new Error('404'))
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.find('[data-account-select]').exists()).toBe(false)

    mocks.api.listProviderAccounts.mockResolvedValue(twoClaude())
    const apiKey = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await apiKey.get('[data-auth-option="ApiKey"]').trigger('click')
    expect(apiKey.find('[data-account-select]').exists()).toBe(false)
  })

  it('sends the chosen account when creating', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-account-select]').setValue('home0002')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({ credentialId: 'home0002' }))
  })

  it('prefills the pinned account when editing and sends it only when changed', async () => {
    const session = {
      id: 's1', title: 'T', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription', credentialId: 'home0002',
      policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }, repos: [], cpu: '500m', memory: '1Gi'
    }
    const wrapper = mount(EditSessionDialog, { props: { session, projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.get('[data-account-select]').element.value).toBe('home0002')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession.mock.calls[0][1]).not.toHaveProperty('credentialId')

    await wrapper.get('[data-account-select]').setValue('work0001')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession.mock.calls[1][1]).toMatchObject({ credentialId: 'work0001' })
  })

  it('does not pin an unpinned session to the preselected default on save', async () => {
    const session = {
      id: 's1', title: 'T', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription', credentialId: null,
      policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }, repos: [], cpu: '500m', memory: '1Gi'
    }
    const wrapper = mount(EditSessionDialog, { props: { session, projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.get('[data-account-select]').element.value).toBe('work0001')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession.mock.calls[0][1]).not.toHaveProperty('credentialId')
  })

  it('drops the pin when the agent changes, since an account belongs to one provider', async () => {
    const session = {
      id: 's1', title: 'T', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription', credentialId: 'home0002',
      policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }, repos: [], cpu: '500m', memory: '1Gi'
    }
    const wrapper = mount(EditSessionDialog, { props: { session, projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    expect(wrapper.find('[data-account-select]').exists()).toBe(false)
    await wrapper.get('[data-submit]').trigger('click')
    // An empty string is the update convention for "back to the default account".
    expect(mocks.api.updateSession.mock.calls[0][1]).toMatchObject({ credentialId: '' })
  })

  it('carries the source account into a duplicate and lets it be changed', async () => {
    const session = {
      id: 's1', title: 'T', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription', credentialId: 'home0002',
      policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }
    }
    const wrapper = mount(DuplicateSessionDialog, { props: { session, projects: [] } })
    await flushPromises()
    expect(wrapper.get('[data-account-select]').element.value).toBe('home0002')
    await wrapper.get('[data-account-select]').setValue('work0001')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ credentialId: 'work0001' }))
  })
})

describe('ProviderAccountsPane', () => {
  it('lists each provider with label, identity and default marker', async () => {
    const wrapper = mount(ProviderAccountsPane, { props: { status: {} } })
    await flushPromises()
    const rows = wrapper.findAll('[data-provider-accounts-for="Claude"] [data-provider-account]')
    expect(rows).toHaveLength(2)
    expect(rows[0].get('[data-account-label]').text()).toBe('Work')
    expect(rows[0].get('[data-account-identity]').text()).toBe('me@example.com · Example Org')
    expect(rows[0].find('[data-account-default]').exists()).toBe(true)
    expect(rows[0].find('[data-account-make-default]').exists()).toBe(false)
    expect(rows[1].find('[data-account-default]').exists()).toBe(false)
    expect(rows[1].find('[data-account-make-default]').exists()).toBe(true)
    expect(wrapper.get('[data-provider-accounts-for="Codex"] [data-credential-status="codexSubscription"]').text())
      .toContain('No Codex subscription login is stored yet')
  })

  it('lists OpenCode logins like every other provider', async () => {
    const go = { id: 'go000001', label: 'Go', email: null, organization: 'opencode-go', isDefault: true, createdAt: '2026-10-09T00:00:00Z' }
    mocks.api.listProviderAccounts.mockResolvedValue({ ...twoClaude(), OpenCode: [go] })
    const wrapper = mount(ProviderAccountsPane, { props: { status: {} } })
    await flushPromises()
    const rows = wrapper.findAll('[data-provider-accounts-for="OpenCode"] [data-provider-account]')
    expect(rows).toHaveLength(1)
    expect(rows[0].get('[data-account-identity]').text()).toBe('opencode-go')
    expect(wrapper.get('[data-provider-accounts-for="OpenCode"] [data-credential-status="opencodeSubscription"]').text())
      .toContain('One OpenCode subscription login is stored')
  })

  it('makes an account the default, renames it and removes it, then re-reads the listing', async () => {
    const wrapper = mount(ProviderAccountsPane, { props: { status: {} } })
    await flushPromises()
    const rows = () => wrapper.findAll('[data-provider-accounts-for="Claude"] [data-provider-account]')

    await rows()[1].get('[data-account-make-default]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateProviderAccount).toHaveBeenCalledWith('Claude', 'home0002', { isDefault: true })

    await rows()[0].get('[data-account-rename]').trigger('click')
    await rows()[0].get('[data-account-label-input]').setValue('Team')
    await rows()[0].get('[data-account-save-label]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateProviderAccount).toHaveBeenCalledWith('Claude', 'work0001', { label: 'Team' })

    mocks.api.listProviderAccounts.mockResolvedValue({ Claude: [work] })
    await rows()[1].get('[data-account-remove]').trigger('click')
    await flushPromises()
    expect(mocks.api.deleteProviderAccount).toHaveBeenCalledWith('Claude', 'home0002')
    expect(rows()).toHaveLength(1)
    expect(wrapper.emitted('changed')).toHaveLength(3)
    expect(mocks.api.listProviderAccounts).toHaveBeenCalledTimes(4)
  })

  it('reports a failed change and keeps the row', async () => {
    mocks.api.deleteProviderAccount.mockRejectedValue(new Error('backend said no'))
    const wrapper = mount(ProviderAccountsPane, { props: { status: {} } })
    await flushPromises()
    await wrapper.findAll('[data-account-remove]')[0].trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-provider-accounts-error]').text()).toContain('backend said no')
    expect(wrapper.findAll('[data-provider-account]')).toHaveLength(2)
  })

  it('falls back to the status booleans with a remove-all control when the listing is unavailable', async () => {
    mocks.api.listProviderAccounts.mockRejectedValue(new Error('404'))
    const wrapper = mount(ProviderAccountsPane, { props: { status: { codexSubscription: true } } })
    await flushPromises()
    expect(wrapper.get('[data-credential-status="codexSubscription"]').text()).toContain('Codex subscription login is stored')
    await wrapper.get('[data-remove-subscription="Codex"]').trigger('click')
    await flushPromises()
    expect(mocks.api.deleteSubscriptionCredential).toHaveBeenCalledWith('Codex')
    expect(wrapper.emitted('changed')).toHaveLength(1)
    expect(wrapper.find('[data-provider-accounts-for="Claude"] [data-remove-subscription]').exists()).toBe(false)
  })

  it('is embedded in the credentials dialog, which re-reads its status on changes', async () => {
    mocks.api.getCredentialStatus
      .mockResolvedValueOnce({ claudeSubscription: true })
      .mockResolvedValueOnce({ claudeSubscription: false })
    mocks.api.listProviderAccounts.mockResolvedValueOnce(twoClaude()).mockResolvedValue({ Claude: [] })
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    expect(wrapper.find('[data-provider-accounts]').exists()).toBe(true)
    await wrapper.findAll('[data-account-remove]')[0].trigger('click')
    await flushPromises()
    expect(mocks.api.getCredentialStatus).toHaveBeenCalledTimes(2)
    expect(wrapper.get('[data-credential-status="claudeSubscription"]').text()).toContain('No Claude subscription login')
  })
})

describe('switching the account of a running session', () => {
  const session = (extra = {}) => ({
    id: 's1', title: 'Coder', phase: 'Running', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription',
    credentialId: 'work0001', ...extra
  })
  const mountTerminal = (props = {}) => mount(TerminalView, {
    props: { session: session(), ...props },
    global: { stubs: { TerminalPane: true, ShareSessionDialog: true } }
  })

  it('offers the owner a dropdown with the current account selected', async () => {
    const wrapper = mountTerminal()
    await flushPromises()
    expect(wrapper.get('[data-account-select]').element.value).toBe('work0001')
    expect(wrapper.find('[data-account-confirm]').exists()).toBe(false)
  })

  it('is offered for an OpenCode session with two logins', async () => {
    const a = { id: 'go000001', label: 'Go', isDefault: true }
    const b = { id: 'go000002', label: 'Go team', isDefault: false }
    mocks.api.listProviderAccounts.mockResolvedValue({ OpenCode: [a, b] })
    const wrapper = mountTerminal({ session: session({ agent: 'OpenCode', credentialId: 'go000002' }) })
    await flushPromises()
    expect(wrapper.get('[data-account-select]').element.value).toBe('go000002')
  })

  it('asks inline before switching and reports the restart afterwards', async () => {
    const wrapper = mountTerminal()
    await flushPromises()
    await wrapper.get('[data-account-select]').setValue('home0002')
    const confirm = wrapper.get('[data-account-confirm]')
    expect(confirm.text()).toContain('Switch this session to “Personal”?')
    expect(mocks.api.switchSessionCredential).not.toHaveBeenCalled()

    await confirm.get('[data-account-confirm-switch]').trigger('click')
    await flushPromises()
    expect(mocks.api.switchSessionCredential).toHaveBeenCalledWith('s1', 'home0002')
    expect(wrapper.find('[data-account-confirm]').exists()).toBe(false)
    expect(wrapper.get('[data-account-note]').text()).toContain('Switched to “Personal”')
    expect(wrapper.get('[data-account-select]').element.value).toBe('home0002')
  })

  it('can be cancelled, and shows the backend reason when the switch fails', async () => {
    mocks.api.switchSessionCredential.mockRejectedValue(new Error('409 The session is not running'))
    const wrapper = mountTerminal()
    await flushPromises()
    await wrapper.get('[data-account-select]').setValue('home0002')
    await wrapper.get('[data-account-cancel]').trigger('click')
    expect(wrapper.find('[data-account-confirm]').exists()).toBe(false)
    expect(wrapper.get('[data-account-select]').element.value).toBe('work0001')

    await wrapper.get('[data-account-select]').setValue('home0002')
    await wrapper.get('[data-account-confirm-switch]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-account-note]').text()).toContain('not running')
    expect(wrapper.get('[data-account-select]').element.value).toBe('work0001')
  })

  it('is absent for viewers, shared links, non-running, API-key and single-login sessions', async () => {
    const cases = [
      { session: session({ accessRole: 'Viewer' }) },
      { session: session(), sharedToken: 'tok' },
      { session: session({ phase: 'Paused' }) },
      { session: session({ authMode: 'ApiKey' }) }
    ]
    for (const props of cases) {
      const wrapper = mountTerminal(props)
      await flushPromises()
      expect(wrapper.find('[data-account-switch]').exists()).toBe(false)
    }
    mocks.api.listProviderAccounts.mockResolvedValue({ Claude: [work] })
    const single = mountTerminal()
    await flushPromises()
    expect(single.find('[data-account-switch]').exists()).toBe(false)
  })
})
