// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionsView from './SessionsView.vue'
import HomeView from './HomeView.vue'

const mocks = vi.hoisted(() => ({
  api: { usageSummary: vi.fn(), usageSessions: vi.fn() }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const sessions = [
  { id: 's1', title: 'Fix checkout', phase: 'Running', mode: 'Autonomous', projectId: 'p1', repoUrl: 'https://x/shop/web.git' },
  { id: 's2', title: 'Nightly deps', phase: 'Running', mode: 'Interactive', questionPending: true, projectId: 'p1' },
  { id: 's3', title: 'Docs pass', phase: 'Paused', mode: 'Interactive', canResume: true },
  { id: 's4', title: 'From a friend', phase: 'Running', mode: 'Interactive', accessRole: 'Viewer', sharedBy: 'jonas' }
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
