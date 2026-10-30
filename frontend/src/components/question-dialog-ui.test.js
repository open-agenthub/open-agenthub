// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import SessionList from './SessionList.vue'
import SessionsView from './SessionsView.vue'
import HomeView from './HomeView.vue'
import TerminalView from './TerminalView.vue'

enableAutoUnmount(afterEach)
vi.mock('../api.js', () => ({
  api: { usageSummary: vi.fn().mockResolvedValue(null), usageSessions: vi.fn().mockResolvedValue([]), getCredentialStatus: vi.fn().mockResolvedValue({}) },
  config: { gitEnabled: false }
}))

// #361: an open question dialog is its own state. questionPending keeps the #343 meaning "your
// turn" and its blinking dot; only questionOpen draws the question marker and "Question open".
const base = { phase: 'Running', mode: 'Interactive', agent: 'Codex', accessRole: 'Owner' }
const asking = { ...base, id: 'q', title: 'Asks', questionOpen: true, questionTitle: 'Which database should the migration target?' }
const yourTurn = { ...base, id: 'p', title: 'Your turn', questionPending: true }
const idle = { ...base, id: 'i', title: 'Idle' }

describe('open question dialog', () => {
  it('marks the sidebar row with the question as tooltip, apart from a pending reply', () => {
    const wrapper = mount(SessionList, { props: { sessions: [asking, yourTurn, idle] } })
    const marker = wrapper.findAll('[data-session-question]')
    expect(marker).toHaveLength(1)
    expect(wrapper.find('[data-session-row="q"] [data-session-question]').attributes('title'))
      .toBe('Question open: Which database should the migration target?')
    expect(wrapper.find('[data-session-row="q"] .st').text()).toBe('Question open')
    expect(wrapper.find('[data-session-row="p"] .dot').classes()).toContain('ask')
    expect(wrapper.find('[data-session-row="p"] .st').text()).toBe('Waiting')
    expect(wrapper.find('[data-session-row="p"] .st').attributes('title')).toBeUndefined()
    expect(wrapper.find('[data-session-row="i"] .dot').classes()).not.toContain('ask')
    expect(wrapper.find('[data-session-row="i"] .st').text()).toBe('Running')
  })

  it('drops the marker once the dialog is answered', async () => {
    const wrapper = mount(SessionList, { props: { sessions: [asking] } })
    await wrapper.setProps({ sessions: [{ ...asking, questionOpen: false, questionTitle: null }] })
    expect(wrapper.find('[data-session-question]').exists()).toBe(false)
    expect(wrapper.find('.st').text()).toBe('Running')
  })

  it('says "Question open" in the session status, with the question as tooltip', () => {
    const wrapper = mount(TerminalView, { props: { session: asking }, global: { stubs: { TerminalPane: true, ShareSessionDialog: true } } })
    const status = wrapper.find('[data-session-status]')
    expect(status.text()).toBe('Question open')
    expect(status.attributes('title')).toBe('Question open: Which database should the migration target?')
  })

  it('filters and counts open questions on the sessions page', async () => {
    const wrapper = mount(SessionsView, { props: { sessions: [asking, yourTurn, idle], projects: [] } })
    expect(wrapper.text()).toContain('2 waiting on you')
    await wrapper.findAll('.chip.filter').find(chip => chip.text() === 'Question open').trigger('click')
    expect(wrapper.findAll('.row-title').map(title => title.text())).toEqual(['Asks'])
  })

  it('lists an open question under NEEDS YOU with its question', async () => {
    const wrapper = mount(HomeView, { props: { sessions: [asking, idle] } })
    await flushPromises()
    expect(wrapper.find('.ny-title').text()).toBe('Asks')
    expect(wrapper.find('.ny-sub').text()).toBe('Question open: Which database should the migration target?')
  })
})
