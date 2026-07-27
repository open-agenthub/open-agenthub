// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionsView from './SessionsView.vue'
import HomeView from './HomeView.vue'

const mocks = vi.hoisted(() => ({
  api: { usageSummary: vi.fn(), usageSessions: vi.fn(), getCredentialStatus: vi.fn().mockResolvedValue({}) },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const sessions = [
  { id: 's1', title: 'Fix checkout', phase: 'Running', mode: 'Autonomous', agent: 'Codex', authMode: 'ApiKey', projectId: 'p1', repoUrl: 'https://x/shop/web.git' },
  { id: 's2', title: 'Nightly deps', phase: 'Running', mode: 'Interactive', questionPending: true, projectId: 'p1' },
  { id: 's3', title: 'Docs pass', phase: 'Paused', mode: 'Interactive', canResume: true },
  { id: 's4', title: 'From a friend', phase: 'Running', mode: 'Interactive', accessRole: 'Viewer', sharedBy: 'jonas' },
  { id: 's5', title: 'Cursor refactor', phase: 'Running', mode: 'Autonomous', agent: 'Cursor', authMode: 'Subscription', projectId: 'p1' }
]
const projects = [{ id: 'p1', name: 'Checkout revamp', sortOrder: 0 }]

describe('SessionsView', () => {
  it('groups sessions by project with shared and ungrouped buckets', () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects } })
    const names = wrapper.findAll('.group-name').map(n => n.text())
    expect(names).toContain('CHECKOUT REVAMP')
    expect(names).toContain('SHARED WITH ME')
    expect(names).toContain('NO PROJECT')
  })

  it('filters by display status including Waiting', async () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects } })
    const waitingChip = wrapper.findAll('.chip.filter').find(c => c.text() === 'Waiting')
    await waitingChip.trigger('click')
    expect(wrapper.findAll('.row-title').map(t => t.text())).toEqual(['Nightly deps'])
  })

  it('regroups by status', async () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects } })
    const statusChip = wrapper.findAll('.group-by .chip').find(c => c.text() === 'Status')
    await statusChip.trigger('click')
    const names = wrapper.findAll('.group-name').map(n => n.text())
    expect(names).toContain('RUNNING')
    expect(names).toContain('WAITING')
    expect(names).toContain('PAUSED')
  })

  it('emits select and duplicate; hides manage actions on shared sessions', async () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects } })
    await wrapper.findAll('.row')[0].trigger('click')
    expect(wrapper.emitted('select')[0]).toEqual(['s1'])
    await wrapper.find('[title="Duplicate session"]').trigger('click')
    expect(wrapper.emitted('duplicate')[0]).toEqual(['s1'])
    const sharedRow = wrapper.findAll('.row').find(r => r.text().includes('From a friend'))
    expect(sharedRow.find('.row-actions').exists()).toBe(false)
  })

  it('applies the search query', () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects, query: 'docs' } })
    expect(wrapper.findAll('.row-title').map(t => t.text())).toEqual(['Docs pass'])
  })

  it('labels agent and billing on every session row', () => {
    const wrapper = mount(SessionsView, { props: { sessions, projects } })
    const row = wrapper.findAll('.row').find(item => item.text().includes('Fix checkout'))
    expect(row.text()).toContain('Codex')
    expect(row.text()).toContain('API key')
    const cursorRow = wrapper.findAll('.row').find(item => item.text().includes('Cursor refactor'))
    expect(cursorRow.text()).toContain('Cursor')
    expect(cursorRow.text()).toContain('Subscription')
  })
})

describe('HomeView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.usageSummary.mockResolvedValue({ costUsd: 12.5, sessionCount: 2 })
    mocks.api.usageSessions.mockResolvedValue([{ sessionId: 's1', title: 'Fix checkout', costUsd: 9 }])
  })

  it('lists waiting sessions under NEEDS YOU and emits select on reply', async () => {
    const wrapper = mount(HomeView, { props: { sessions } })
    await flushPromises()
    expect(wrapper.text()).toContain('NEEDS YOU')
    expect(wrapper.find('.ny-title').text()).toBe('Nightly deps')
    await wrapper.find('.ny-actions button').trigger('click')
    expect(wrapper.emitted('select')[0]).toEqual(['s2'])
  })

  it('shows the calm state when nothing is waiting', async () => {
    const wrapper = mount(HomeView, { props: { sessions: sessions.filter(s => !s.questionPending) } })
    await flushPromises()
    expect(wrapper.text()).toContain('NOTHING NEEDS YOU')
  })

  it('survives a missing usage API', async () => {
    mocks.api.usageSummary.mockRejectedValue(new Error('disabled'))
    const wrapper = mount(HomeView, { props: { sessions } })
    await flushPromises()
    expect(wrapper.text()).toContain('NEEDS YOU')
  })
})

import SessionSearch from './SessionSearch.vue'

describe('SessionSearch', () => {
  const list = [
    { id: 'a', title: 'Alpha task', phase: 'Running' },
    { id: 'b', title: 'Beta task', phase: 'Paused' },
    { id: 'c', title: 'Gamma other', phase: 'Running' }
  ]

  it('shows matching sessions in a dropdown', async () => {
    const wrapper = mount(SessionSearch, { props: { sessions: list, modelValue: 'task', 'onUpdate:modelValue': v => wrapper.setProps({ modelValue: v }) } })
    await wrapper.get('input').trigger('focus')
    expect(wrapper.findAll('[data-search-result]')).toHaveLength(2)
  })

  it('navigates with arrow keys and opens the highlighted session on Enter', async () => {
    const wrapper = mount(SessionSearch, { props: { sessions: list, modelValue: 'task', 'onUpdate:modelValue': v => wrapper.setProps({ modelValue: v }) } })
    const input = wrapper.get('input')
    await input.trigger('focus')
    await input.trigger('keydown', { key: 'ArrowDown' })
    expect(wrapper.findAll('[data-search-result]')[1].classes()).toContain('hl')
    await input.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('select')[0]).toEqual(['b'])
  })

  it('wraps the highlight and supports ArrowUp', async () => {
    const wrapper = mount(SessionSearch, { props: { sessions: list, modelValue: 'task', 'onUpdate:modelValue': v => wrapper.setProps({ modelValue: v }) } })
    const input = wrapper.get('input')
    await input.trigger('focus')
    await input.trigger('keydown', { key: 'ArrowUp' })
    expect(wrapper.findAll('[data-search-result]')[1].classes()).toContain('hl')
  })

  it('selects on click', async () => {
    const wrapper = mount(SessionSearch, { props: { sessions: list, modelValue: 'gamma' } })
    await wrapper.get('input').trigger('focus')
    await wrapper.get('[data-search-result="c"]').trigger('mousedown')
    expect(wrapper.emitted('select')[0]).toEqual(['c'])
  })
})

import TerminalView from './TerminalView.vue'

describe('TerminalView session identity', () => {
  it('labels the selected agent and billing source in session detail', () => {
    const wrapper = mount(TerminalView, {
      props: { session: sessions[0] },
      global: { stubs: { TerminalPane: true, ShareSessionDialog: true } }
    })
    expect(wrapper.find('.meta').text()).toContain('Codex')
    expect(wrapper.find('.meta').text()).toContain('API key')
  })

  it('labels Cursor agent and billing in session detail', () => {
    const wrapper = mount(TerminalView, {
      props: { session: sessions[4] },
      global: { stubs: { TerminalPane: true, ShareSessionDialog: true } }
    })
    expect(wrapper.find('.meta').text()).toContain('Cursor')
    expect(wrapper.find('.meta').text()).toContain('Subscription')
  })

  it('renders the chat pane instead of the terminal for chat sessions and drops the Transcript tab', () => {
    const stubs = { TerminalPane: true, ChatPane: true, ShareSessionDialog: true }
    const chat = mount(TerminalView, {
      props: { session: { id: 'c1', title: 'Chat', phase: 'Running', mode: 'Interactive', agent: 'Claude', uiMode: 'chat' } },
      global: { stubs }
    })
    expect(chat.findComponent({ name: 'ChatPane' }).exists()).toBe(true)
    expect(chat.findComponent({ name: 'TerminalPane' }).exists()).toBe(false)
    expect(chat.findAll('.tabs button').map(b => b.text())).not.toContain('Transcript')

    const terminal = mount(TerminalView, { props: { session: sessions[0] }, global: { stubs } })
    expect(terminal.findComponent({ name: 'TerminalPane' }).exists()).toBe(true)
    expect(terminal.findComponent({ name: 'ChatPane' }).exists()).toBe(false)
    expect(terminal.findAll('.tabs button').map(b => b.text())).toContain('Transcript')
  })
})

import NewSessionDialog from './NewSessionDialog.vue'

describe('NewSessionDialog interface choice', () => {
  const mountDialog = () => mount(NewSessionDialog, {
    props: { embedded: true },
    global: { stubs: { RepoPicker: true, AgentDecisionCard: true } }
  })

  it('offers Terminal/Chat only for interactive Claude sessions', async () => {
    const wrapper = mountDialog()
    expect(wrapper.findAll('[data-ui-mode-option]').map(b => b.text())).toEqual(['Terminal', 'Chat'])

    const autonomous = wrapper.findAll('[data-mode-option]').find(b => b.text() === 'Autonomous')
    await autonomous.trigger('click')
    expect(wrapper.findAll('[data-ui-mode-option]')).toHaveLength(0)
  })

  it('submits the chosen uiMode and resets it when chat becomes unavailable', async () => {
    mocks.api.createSession = vi.fn().mockResolvedValue({ id: 'new' })
    const wrapper = mountDialog()
    const chatChip = wrapper.findAll('[data-ui-mode-option]').find(b => b.text() === 'Chat')
    await chatChip.trigger('click')
    await wrapper.find('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSession.mock.calls[0][0].uiMode).toBe('chat')

    await chatChip.trigger('click')
    const autonomous = wrapper.findAll('[data-mode-option]').find(b => b.text() === 'Autonomous')
    await autonomous.trigger('click')
    const interactive = wrapper.findAll('[data-mode-option]').find(b => b.text() === 'Interactive')
    await interactive.trigger('click')
    await wrapper.find('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSession.mock.calls[1][0].uiMode).toBe('terminal')
  })
})

import CredentialsDialog from './CredentialsDialog.vue'

describe('CredentialsDialog git hint', () => {
  it('points at account connect when git OAuth is configured', () => {
    mocks.config.gitEnabled = true
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    expect(wrapper.find('[data-git-hint="connect"]').exists()).toBe(true)
    wrapper.get('[data-git-hint="connect"] button').trigger('click')
    expect(wrapper.emitted('accounts')).toBeTruthy()
  })

  it('explains the Helm values when git OAuth is not configured', () => {
    mocks.config.gitEnabled = false
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    expect(wrapper.find('[data-git-hint="helm"]').text()).toContain('git.providers')
  })
})
