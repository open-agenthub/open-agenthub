// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ProviderAccountsPane from './ProviderAccountsPane.vue'
import AgentDecisionCard from './AgentDecisionCard.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import TerminalView from './TerminalView.vue'
import { accountLimitLabel, availableAlternatives, isAccountExhausted } from '../lib/agent.js'

// Accounts at their usage limit (docs/account-limits.md): the settings pane's badge and
// "clear limit", the marked dropdown entries, the session view's banners after the hub moved
// a session or could not, and the per-session failover switch.

const mocks = vi.hoisted(() => ({
  api: {
    createSession: vi.fn(), updateSession: vi.fn(), duplicateSession: vi.fn(),
    getCredentialStatus: vi.fn(), storeCredentials: vi.fn(), deleteSubscriptionCredential: vi.fn(),
    getAllowedAgents: vi.fn(), mcpServers: vi.fn(),
    listProviderAccounts: vi.fn(), updateProviderAccount: vi.fn(), deleteProviderAccount: vi.fn(),
    switchSessionCredential: vi.fn(),
    getTranscript: vi.fn().mockResolvedValue(''), listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]), decidePermission: vi.fn(),
    getConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 })
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({
  api: mocks.api, config: mocks.config,
  getSharedTranscript: vi.fn().mockResolvedValue(''),
  getSharedConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 })
}))

const until = new Date(Date.now() + 2 * 3600 * 1000).toISOString()
const work = { id: 'work0001', label: 'Work', email: 'me@example.com', isDefault: true, createdAt: '2026-10-01T00:00:00Z',
  isExhausted: true, exhaustedUntil: until, exhaustedReason: 'mod five_hour 100%' }
const personal = { id: 'home0002', label: 'Personal', isDefault: false, createdAt: '2026-10-02T00:00:00Z', isExhausted: false }
const accounts = (list = [work, personal]) => ({ Claude: list, Codex: [], Cursor: [], OpenClaw: [] })

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.getCredentialStatus.mockResolvedValue({ claudeSubscription: true })
  mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw'] })
  mocks.api.mcpServers.mockResolvedValue([])
  mocks.api.listProviderAccounts.mockResolvedValue(accounts())
  mocks.api.updateProviderAccount.mockResolvedValue(null)
  mocks.api.updateSession.mockResolvedValue({ id: 's1' })
  mocks.api.switchSessionCredential.mockResolvedValue({ id: 's1' })
})

describe('limit helpers', () => {
  it('read the listing flags and say when the account comes back', () => {
    expect(isAccountExhausted(work)).toBe(true)
    expect(isAccountExhausted(personal)).toBe(false)
    expect(isAccountExhausted(null)).toBe(false)
    expect(accountLimitLabel(work)).toMatch(/^at limit · resets \d/)
    expect(accountLimitLabel({ isExhausted: true })).toBe('at limit')
    expect(accountLimitLabel(personal)).toBe('')
    expect(availableAlternatives([work, personal], 'work0001').map(a => a.id)).toEqual(['home0002'])
    expect(availableAlternatives([work, personal], 'home0002')).toEqual([])
  })
})

describe('the provider accounts pane', () => {
  it('badges an exhausted account and clears the mark through the account PATCH', async () => {
    const wrapper = mount(ProviderAccountsPane, { props: { status: { claudeSubscription: true } } })
    await flushPromises()

    const row = wrapper.get('[data-provider-account="work0001"]')
    expect(row.get('[data-account-exhausted]').text()).toMatch(/^at limit/)
    expect(row.get('[data-account-exhausted]').attributes('title')).toBe('mod five_hour 100%')
    expect(wrapper.get('[data-provider-account="home0002"]').find('[data-account-exhausted]').exists()).toBe(false)
    expect(wrapper.get('[data-provider-account="home0002"]').find('[data-account-clear-exhausted]').exists()).toBe(false)

    await row.get('[data-account-clear-exhausted]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateProviderAccount).toHaveBeenCalledWith('Claude', 'work0001', { clearExhausted: true })
    expect(wrapper.emitted('changed')).toBeTruthy()
  })
})

describe('the account dropdown in the session dialogs', () => {
  it('marks an exhausted account but still lets it be picked', async () => {
    const wrapper = mount(AgentDecisionCard, {
      props: { agent: 'Claude', authMode: 'Subscription', mode: 'Interactive', accounts: accounts(), credentialId: '' }
    })
    await flushPromises()

    const options = wrapper.findAll('[data-account-option]')
    expect(options[0].attributes('data-account-exhausted')).toBe('true')
    expect(options[0].text()).toMatch(/Work — me@example.com \(default\) \(at limit/)
    expect(options[1].attributes('data-account-exhausted')).toBeUndefined()
    expect(wrapper.text()).toContain('skipped for new sessions unless you pick it here')
  })
})

describe('the session view', () => {
  const session = (extra = {}) => ({
    id: 's1', title: 'Coder', phase: 'Running', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription',
    credentialId: 'work0001', accountFailover: 'auto', ...extra
  })
  const mountTerminal = (props = {}) => mount(TerminalView, {
    props: { session: session(), ...props },
    global: { stubs: { TerminalPane: true, ShareSessionDialog: true } }
  })

  it('marks the exhausted account in the header dropdown', async () => {
    const wrapper = mountTerminal()
    await flushPromises()
    const options = wrapper.findAll('[data-account-select] [data-account-option]')
    expect(options[0].attributes('data-account-exhausted')).toBe('true')
    expect(options[0].text()).toMatch(/at limit/)
    expect(options[1].attributes('data-account-exhausted')).toBeUndefined()
  })

  it('shows a banner when the hub moved the session to another account', async () => {
    const wrapper = mountTerminal()
    await flushPromises()
    expect(wrapper.find('[data-account-switched]').exists()).toBe(false)

    await wrapper.setProps({ session: session({ credentialId: 'home0002' }) })
    await flushPromises()
    const banner = wrapper.get('[data-account-switched]')
    expect(banner.text()).toContain('Account switched to “Personal”')
    expect(wrapper.get('[data-account-select]').element.value).toBe('home0002')

    await banner.get('[data-account-switched-dismiss]').trigger('click')
    expect(wrapper.find('[data-account-switched]').exists()).toBe(false)
  })

  it('does not mistake a switch made from the dropdown for an automatic one', async () => {
    const wrapper = mountTerminal()
    await flushPromises()
    await wrapper.get('[data-account-select]').setValue('home0002')
    await wrapper.get('[data-account-confirm-switch]').trigger('click')
    await flushPromises()
    await wrapper.setProps({ session: session({ credentialId: 'home0002' }) })
    await flushPromises()
    expect(wrapper.find('[data-account-switched]').exists()).toBe(false)
    expect(wrapper.get('[data-account-note]').text()).toContain('Switched to “Personal”')
  })

  it('says when the account is at its limit, and whether another one could take over', async () => {
    const withAlternative = mountTerminal()
    await flushPromises()
    const banner = withAlternative.get('[data-account-at-limit]')
    expect(banner.attributes('data-has-alternative')).toBe('true')
    expect(banner.text()).toContain('Account at limit.')
    expect(banner.text()).toContain('Another account is available')

    const switchedOff = mountTerminal({ session: session({ accountFailover: 'off' }) })
    await flushPromises()
    expect(switchedOff.get('[data-account-at-limit]').text()).toContain('Automatic switching is off')

    mocks.api.listProviderAccounts.mockResolvedValue(accounts([work, { ...personal, isExhausted: true }]))
    const none = mountTerminal()
    await flushPromises()
    expect(none.get('[data-account-at-limit]').attributes('data-has-alternative')).toBe('false')
    expect(none.get('[data-account-at-limit]').text()).toContain('no other account available')

    // The session runs on an account that is fine: nothing to say.
    mocks.api.listProviderAccounts.mockResolvedValue(accounts())
    const fine = mountTerminal({ session: session({ credentialId: 'home0002' }) })
    await flushPromises()
    expect(fine.find('[data-account-at-limit]').exists()).toBe(false)
  })

  it('uses the account the hub resolved for an unpinned session', async () => {
    const wrapper = mountTerminal({ session: session({ credentialId: null, resolvedCredentialId: 'home0002' }) })
    await flushPromises()
    expect(wrapper.get('[data-account-select]').element.value).toBe('home0002')
    expect(wrapper.find('[data-account-at-limit]').exists()).toBe(false)
  })
})

describe('the edit dialog', () => {
  const session = (extra = {}) => ({
    id: 's1', title: 'Coder', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription', credentialId: 'work0001',
    repos: [], mcpServerIds: [], ...extra
  })
  const mountEdit = (extra = {}) => mount(EditSessionDialog, {
    props: { session: session(extra), projects: [] },
    global: { stubs: { RepoPicker: { template: '<div data-repo-picker></div>' } } }
  })

  it('shows the failover switch on, and sends it only when it changed', async () => {
    const wrapper = mountEdit()
    await flushPromises()
    const toggle = wrapper.get('[data-account-failover]')
    expect(toggle.attributes('aria-checked')).toBe('true')

    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSession.mock.calls[0][1]).not.toHaveProperty('accountFailover')

    await toggle.trigger('click')
    expect(toggle.attributes('aria-checked')).toBe('false')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSession.mock.calls[1][1].accountFailover).toBe('off')
  })

  it('prefills off from the session and hides the switch for API-key sessions', async () => {
    const off = mountEdit({ accountFailover: 'off' })
    await flushPromises()
    expect(off.get('[data-account-failover]').attributes('aria-checked')).toBe('false')
    await off.get('[data-account-failover]').trigger('click')
    await off.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSession.mock.calls[0][1].accountFailover).toBe('auto')

    const apiKey = mountEdit({ authMode: 'ApiKey' })
    await flushPromises()
    expect(apiKey.find('[data-account-failover]').exists()).toBe(false)
  })
})
