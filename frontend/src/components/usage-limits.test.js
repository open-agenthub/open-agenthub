// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import UsageView from './UsageView.vue'
import AdminLimitsView from './AdminLimitsView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    usageSummary: vi.fn(),
    usageSessions: vi.fn(),
    usageLimit: vi.fn(),
    setUsageLimit: vi.fn(),
    eeListLimits: vi.fn(),
    eeSetLimit: vi.fn(),
    eeDeleteLimit: vi.fn(),
    eeListGroups: vi.fn(),
    eeSetGroupRole: vi.fn(),
    adminGetAllowedAgents: vi.fn(),
    adminSetAllowedAgents: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const summary = {
  sessionCount: 2,
  inputTokens: 1000, outputTokens: 500, cacheReadTokens: 0, cacheCreationTokens: 0,
  costUsd: 1.25, apiCostUsd: 1.25,
  estimatedCostUsd: 6.25, subscriptionEstimatedCostUsd: 5,
  apiSessionCount: 1, subscriptionSessionCount: 1, subscriptionTokens: 900, subscriptionTurns: 4,
  cpuSeconds: 90, rxBytes: 1048576, txBytes: 2048
}
const rows = [
  { sessionId: 'api-1', title: 'API session', inputTokens: 100, costUsd: 1.25, apiCostUsd: 1.25, costReported: true, estimatedCostUsd: 1.25, authMode: 'ApiKey', billing: 'api', apiBilled: true, source: 'claude-otel', cpuSeconds: 90, memoryBytes: 100, peakMemoryBytes: 536870912, rxBytes: 1048576, txBytes: 2048 },
  // Claude Code reports a price-table cost for subscription logins too; it must not show as money.
  { sessionId: 'sub-1', title: 'Sub session', inputTokens: 900, costUsd: 0.75, apiCostUsd: 0, costReported: true, estimatedCostUsd: 5, authMode: 'Subscription', billing: 'subscription', apiBilled: false, source: 'codex', turns: 4 }
]
const limitOk = { personalLimitUsd: 10, effectiveLimitUsd: 10, source: 'personal', monthApiCostUsd: 1.25, blocked: false }

describe('UsageView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.usageSummary.mockResolvedValue(summary)
    mocks.api.usageSessions.mockResolvedValue(rows)
    mocks.api.usageLimit.mockResolvedValue(limitOk)
  })

  it('shows API cost as money and subscription use as tokens and turns', async () => {
    const wrapper = mount(UsageView)
    await flushPromises()
    expect(wrapper.get('[data-api-cost]').text()).toContain('$1.25')
    const sub = wrapper.get('[data-subscription-usage]').text()
    expect(sub).toContain('900 tokens')
    expect(sub).toContain('4 turns')
    expect(sub).not.toContain('$')
  })

  it('shows money only on API rows, with the CLI the numbers come from', async () => {
    const wrapper = mount(UsageView)
    await flushPromises()
    const rowsText = wrapper.findAll('.trow').map(r => r.text())
    const subRow = rowsText.find(t => t.includes('Sub session'))
    expect(subRow).toContain('sub')
    expect(subRow).toContain('4 turns')
    expect(subRow).toContain('Codex')
    expect(subRow).not.toContain('$')
    expect(rowsText.find(t => t.includes('API session'))).toContain('$1.25')
    expect(rowsText.find(t => t.includes('API session'))).toContain('Claude Code')
    expect(wrapper.get('[data-billing="api"]').attributes('title')).toContain('reported by Claude Code')
  })

  it('shows pod resource totals and per-session resources', async () => {
    const wrapper = mount(UsageView)
    await flushPromises()
    const compute = wrapper.get('[data-compute]').text()
    expect(compute).toContain('1m 30s')          // summary CPU time
    expect(compute).toContain('↓1 MB ↑2 KB')     // summary network
    const rowsText = wrapper.findAll('.trow').map(r => r.text())
    expect(rowsText.find(t => t.includes('API session'))).toContain('1m 30s · 512 MB · ↓1 MB ↑2 KB')
    expect(rowsText.find(t => t.includes('Sub session'))).toContain('—') // no resource data yet
  })

  it('shows the monthly budget with month-to-date spend', async () => {
    const wrapper = mount(UsageView)
    await flushPromises()
    const budget = wrapper.get('[data-usage-limit]').text()
    expect(budget).toContain('$1.25 of $10.00')
  })

  it('flags a blocked budget and names the admin source', async () => {
    mocks.api.usageLimit.mockResolvedValue({
      personalLimitUsd: null, effectiveLimitUsd: 5, source: 'group', monthApiCostUsd: 5, blocked: true
    })
    const wrapper = mount(UsageView)
    await flushPromises()
    const budget = wrapper.get('[data-usage-limit]').text()
    expect(budget).toContain('set by your admin (group)')
    expect(budget).toContain('blocked')
  })

  it('saves the personal limit (and clears it with an empty input)', async () => {
    mocks.api.setUsageLimit.mockResolvedValue(limitOk)
    const wrapper = mount(UsageView)
    await flushPromises()
    await wrapper.get('[data-usage-limit] input').setValue('25')
    await wrapper.get('[data-save-limit]').trigger('click')
    expect(mocks.api.setUsageLimit).toHaveBeenCalledWith(25)

    await wrapper.get('[data-usage-limit] input').setValue('')
    await wrapper.get('[data-save-limit]').trigger('click')
    expect(mocks.api.setUsageLimit).toHaveBeenLastCalledWith(null)
  })

  it('rejects a negative limit locally', async () => {
    const wrapper = mount(UsageView)
    await flushPromises()
    await wrapper.get('[data-usage-limit] input').setValue('-3')
    await wrapper.get('[data-save-limit]').trigger('click')
    expect(mocks.api.setUsageLimit).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('non-negative')
  })
})

describe('AdminLimitsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.eeListLimits.mockResolvedValue([
      { scope: 'global', target: '', limitUsd: 100 },
      { scope: 'group', target: 'devs', limitUsd: 50 },
      { scope: 'user', target: 'alice', limitUsd: 10 }
    ])
    mocks.api.eeListGroups.mockResolvedValue([
      { name: 'devs', role: null, memberCount: 3 },
      { name: 'leads', role: 'admin', memberCount: 1 }
    ])
    mocks.api.adminGetAllowedAgents.mockResolvedValue({ agents: [] })
    mocks.api.adminSetAllowedAgents.mockResolvedValue({ agents: [] })
  })

  it('shows the license gate without a license (402)', async () => {
    const err = new Error('402 payment required'); err.status = 402
    mocks.api.eeListLimits.mockRejectedValue(err)
    mocks.api.eeListGroups.mockRejectedValue(err)
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    const locked = wrapper.get('[data-limits-locked]')
    expect(locked.attributes('data-license-gate')).toBeDefined()
    expect(locked.text()).toContain('agent allowlist')
    expect(wrapper.find('[data-allowed-agents]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('402')
  })

  it('swaps the pane for the gate when saving the allowlist is refused with 402', async () => {
    const err = new Error('402 payment required'); err.status = 402
    mocks.api.adminSetAllowedAgents.mockRejectedValue(err)
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    await wrapper.get('[data-allowed-agent="Claude"] input').setValue(true)
    await wrapper.get('[data-save-allowed-agents]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-limits-locked]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('402')
  })

  it('renders global, group and user limits plus groups with roles', async () => {
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    expect(wrapper.get('[data-global-limit]').text()).toContain('$100.00')
    const scoped = wrapper.get('[data-scoped-limits]').text()
    expect(scoped).toContain('devs')
    expect(scoped).toContain('alice')
    const groups = wrapper.get('[data-group-roles]')
    expect(groups.text()).toContain('3 members')
    expect(groups.get('[data-group-row="leads"] select').element.value).toBe('admin')
  })

  it('adds a scoped limit', async () => {
    mocks.api.eeSetLimit.mockResolvedValue([])
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    await wrapper.get('[data-limit-target]').setValue('bob')
    await wrapper.get('[data-limit-amount]').setValue('7.5')
    await wrapper.get('[data-add-limit]').trigger('click')
    expect(mocks.api.eeSetLimit).toHaveBeenCalledWith({ scope: 'user', target: 'bob', limitUsd: 7.5 })
  })

  it('maps a group to a role', async () => {
    mocks.api.eeSetGroupRole.mockResolvedValue([])
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    await wrapper.get('[data-group-row="devs"] select').setValue('admin')
    expect(mocks.api.eeSetGroupRole).toHaveBeenCalledWith('devs', 'admin')
  })

  it('maps a group nobody has signed in with yet', async () => {
    mocks.api.eeSetGroupRole.mockResolvedValue([{ name: 'new-admins', role: 'admin', memberCount: 0 }])
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    await wrapper.get('[data-new-group]').setValue(' new-admins ')
    await wrapper.get('[data-add-group-role]').trigger('click')
    await flushPromises()
    expect(mocks.api.eeSetGroupRole).toHaveBeenCalledWith('new-admins', 'admin')
    expect(wrapper.get('[data-group-row="new-admins"] select').element.value).toBe('admin')
    expect(wrapper.get('[data-new-group]').element.value).toBe('')
  })

  it('shows a refused group change and restores the stored role', async () => {
    mocks.api.eeSetGroupRole.mockRejectedValue(Object.assign(new Error('409 {"error":"This would leave no admin."}'), { status: 409 }))
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    await wrapper.get('[data-group-row="leads"] select').setValue('')
    await flushPromises()
    expect(wrapper.get('.err').text()).toBe('This would leave no admin.')
    expect(wrapper.get('[data-group-row="leads"] select').element.value).toBe('admin')
  })

  it('loads allowed-agent checkboxes from the admin API', async () => {
    mocks.api.adminGetAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex'] })
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    const panel = wrapper.get('[data-allowed-agents]')
    expect(panel.text()).toContain('Allowed agents')
    expect(wrapper.get('[data-allowed-agent="Claude"] input').element.checked).toBe(true)
    expect(wrapper.get('[data-allowed-agent="Codex"] input').element.checked).toBe(true)
    expect(wrapper.get('[data-allowed-agent="Cursor"] input').element.checked).toBe(false)
    expect(wrapper.get('[data-allowed-agent="OpenClaw"] input').element.checked).toBe(false)
  })

  it('saves the checked allowed-agent list', async () => {
    mocks.api.adminGetAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex'] })
    mocks.api.adminSetAllowedAgents.mockResolvedValue({ agents: ['Claude', 'OpenClaw'] })
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    await wrapper.get('[data-allowed-agent="Codex"] input').setValue(false)
    await wrapper.get('[data-allowed-agent="OpenClaw"] input').setValue(true)
    await wrapper.get('[data-save-allowed-agents]').trigger('click')
    expect(mocks.api.adminSetAllowedAgents).toHaveBeenCalledWith(['Claude', 'OpenClaw'])
  })

  it('shows a 403 error from the allowed-agents API', async () => {
    mocks.api.adminGetAllowedAgents.mockResolvedValue({ agents: [] })
    const err = new Error('403 Forbidden'); err.status = 403
    mocks.api.adminSetAllowedAgents.mockRejectedValue(err)
    const wrapper = mount(AdminLimitsView)
    await flushPromises()
    await wrapper.get('[data-allowed-agent="Claude"] input').setValue(true)
    await wrapper.get('[data-save-allowed-agents]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('403 Forbidden')
  })
})
