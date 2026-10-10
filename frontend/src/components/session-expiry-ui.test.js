// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import DuplicateSessionDialog from './DuplicateSessionDialog.vue'
import TerminalView from './TerminalView.vue'
import SessionList from './SessionList.vue'
import SessionsView from './SessionsView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    createSession: vi.fn(), updateSession: vi.fn(), duplicateSession: vi.fn(),
    getCredentialStatus: vi.fn(), getAllowedAgents: vi.fn(), mcpServers: vi.fn(),
    listProviderAccounts: vi.fn(),
    getTranscript: vi.fn().mockResolvedValue(''),
    getConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 }),
    listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([])
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({
  api: mocks.api, config: mocks.config,
  getSharedTranscript: vi.fn().mockResolvedValue(''),
  getSharedConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 })
}))

const mountOptions = { global: { stubs: { RepoPicker: { template: '<div data-repo-picker></div>' } } } }
const NOW = Date.parse('2026-10-10T12:00:00Z')
const inMs = ms => new Date(NOW + ms).toISOString()
const baseSession = {
  id: 's1', title: 'Existing', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription',
  policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }, repos: [], cpu: '500m', memory: '1Gi'
}

describe('auto-delete in the session dialogs', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers({ now: NOW, toFake: ['Date'] })
    mocks.api.getCredentialStatus.mockResolvedValue({})
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw'] })
    mocks.api.mcpServers.mockResolvedValue([])
    mocks.api.listProviderAccounts.mockResolvedValue({})
    mocks.api.createSession.mockResolvedValue({ id: 'new' })
    mocks.api.updateSession.mockResolvedValue({ id: 's1' })
    mocks.api.duplicateSession.mockResolvedValue({ id: 'copy' })
  })
  afterEach(() => vi.useRealTimers())

  it('creates without a deadline by default, and with seconds and a basis when switched on', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.find('[data-auto-delete-amount]').exists()).toBe(false)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0]).toMatchObject({ autoDeleteAfterSeconds: null, autoDeleteFrom: null })

    mocks.api.createSession.mockClear()
    await wrapper.get('[data-auto-delete-toggle]').trigger('click')
    await wrapper.get('[data-auto-delete-amount]').setValue('3')
    await wrapper.get('[data-auto-delete-unit="days"]').trigger('click')
    await wrapper.get('[data-auto-delete-from="start"]').trigger('click')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0]).toMatchObject({ autoDeleteAfterSeconds: 259200, autoDeleteFrom: 'start' })
  })

  it('locks a scheduled session to the start basis', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-auto-delete-toggle]').trigger('click')
    expect(wrapper.get('[data-auto-delete-from="lastActivity"]').classes()).toContain('on')

    await wrapper.findAll('[data-mode-option]').find(b => b.text() === 'Scheduled').trigger('click')
    expect(wrapper.get('[data-auto-delete-from="lastActivity"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-auto-delete-from="start"]').classes()).toContain('on')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0]).toMatchObject({ autoDeleteFrom: 'start' })
  })

  it('prefills the edit dialog, shows the remaining time, and sends 0 to switch off', async () => {
    const session = { ...baseSession, autoDeleteAfterSeconds: 172800, autoDeleteFrom: 'start', expiresAt: inMs(2 * 86400e3 + 3 * 3600e3) }
    const wrapper = mount(EditSessionDialog, { props: { session, projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.get('[data-auto-delete-toggle]').attributes('aria-checked')).toBe('true')
    expect(wrapper.get('[data-auto-delete-amount]').element.value).toBe('2')
    expect(wrapper.get('[data-auto-delete-unit="days"]').classes()).toContain('on')
    expect(wrapper.get('[data-auto-delete-from="start"]').classes()).toContain('on')
    expect(wrapper.get('[data-auto-delete-expires]').text()).toContain('Expires in 2 d 3 h')

    await wrapper.get('[data-auto-delete-amount]').setValue('12')
    await wrapper.get('[data-auto-delete-unit="hours"]').trigger('click')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ autoDeleteAfterSeconds: 43200, autoDeleteFrom: 'start' }))

    mocks.api.updateSession.mockClear()
    await wrapper.get('[data-auto-delete-toggle]').trigger('click')
    await wrapper.get('[data-submit]').trigger('click')
    // null would mean "unchanged"; 0 is how the API is told to switch the deadline off.
    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ autoDeleteAfterSeconds: 0, autoDeleteFrom: null }))
  })

  it('lets a scheduled session take a start-based deadline through its reduced edit body', async () => {
    const session = { ...baseSession, mode: 'Scheduled', schedule: '0 6 * * *' }
    const wrapper = mount(EditSessionDialog, { props: { session, projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-auto-delete-toggle]').trigger('click')
    expect(wrapper.get('[data-auto-delete-from="lastActivity"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-auto-delete-amount]').setValue('1')
    await wrapper.get('[data-auto-delete-unit="days"]').trigger('click')
    await wrapper.get('[data-submit]').trigger('click')
    const body = mocks.api.updateSession.mock.calls[0][1]
    expect(body).toMatchObject({ autoDeleteAfterSeconds: 86400, autoDeleteFrom: 'start' })
    expect(body).not.toHaveProperty('image')
  })

  it('duplicates with the source setting prefilled and sends 0 when cleared', async () => {
    const session = { ...baseSession, autoDeleteAfterSeconds: 43200, autoDeleteFrom: 'lastActivity' }
    const wrapper = mount(DuplicateSessionDialog, { props: { session, projects: [] } })
    await flushPromises()
    expect(wrapper.get('[data-auto-delete-amount]').element.value).toBe('12')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ autoDeleteAfterSeconds: 43200, autoDeleteFrom: 'lastActivity' }))

    mocks.api.duplicateSession.mockClear()
    await wrapper.get('[data-auto-delete-toggle]').trigger('click')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ autoDeleteAfterSeconds: 0 }))
  })
})

describe('the expiry badge', () => {
  beforeEach(() => vi.useFakeTimers({ now: NOW, toFake: ['Date'] }))
  afterEach(() => vi.useRealTimers())

  const stubs = { TerminalPane: true, ShareSessionDialog: true, SessionWorkspace: { template: '<div><slot /></div>' } }

  it('shows the remaining time in the session header, in the warning colour under an hour', () => {
    const far = mount(TerminalView, { props: { session: { ...baseSession, expiresAt: inMs(5 * 3600e3), autoDeleteFrom: 'lastActivity' } }, global: { stubs } })
    expect(far.get('[data-expires-at]').text()).toContain('expires in 5 h')
    expect(far.get('[data-expires-at]').classes()).not.toContain('soon')

    const near = mount(TerminalView, { props: { session: { ...baseSession, expiresAt: inMs(20 * 60e3) } }, global: { stubs } })
    expect(near.get('[data-expires-at]').text()).toContain('expires in 20 min')
    expect(near.get('[data-expires-at]').classes()).toContain('soon')

    const kept = mount(TerminalView, { props: { session: baseSession }, global: { stubs } })
    expect(kept.find('[data-expires-at]').exists()).toBe(false)
  })

  it('shows it in the sidebar list and the sessions page', () => {
    const sessions = [
      { ...baseSession, id: 'a', title: 'Soon', phase: 'Running', expiresAt: inMs(30 * 60e3) },
      { ...baseSession, id: 'b', title: 'Later', phase: 'Running', expiresAt: inMs(3 * 86400e3) },
      { ...baseSession, id: 'c', title: 'Kept', phase: 'Running' }
    ]
    const list = mount(SessionList, { props: { sessions, active: null } })
    const listBadges = list.findAll('[data-expires-at]')
    expect(listBadges).toHaveLength(2)
    expect(listBadges[0].text()).toContain('30 min')
    expect(listBadges[0].classes()).toContain('soon')
    expect(listBadges[1].text()).toContain('3 d')

    const view = mount(SessionsView, { props: { sessions, projects: [] } })
    const viewBadges = view.findAll('[data-expires-at]')
    expect(viewBadges).toHaveLength(2)
    expect(viewBadges.map(b => b.classes().includes('soon'))).toEqual([true, false])
  })
})
