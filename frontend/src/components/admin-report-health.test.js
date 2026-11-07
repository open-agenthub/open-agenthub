import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
const mocks = vi.hoisted(() => ({ adminOverview: vi.fn() }))
vi.mock('../api.js', () => ({ api: mocks, auth: {} }))
import AdminView from './AdminView.vue'

const overview = (extra = {}) => ({
  license: { present: true, valid: true, seats: 5, plan: 'subscription' },
  seats: { used: 1, included: 5 }, users: [], lastCheckIn: null,
  featuresStopAt: '2026-11-10T12:00:00Z', lastReportError: 'License service rejected the report (HTTP 503).',
  lastReportErrorAt: '2026-11-07T12:00:00Z', reportWarning: true, ...extra
})

beforeEach(() => vi.clearAllMocks())
describe('licence report health', () => {
  it('shows never-reported state, signed stop date and the persisted error on the license tab', async () => {
    mocks.adminOverview.mockResolvedValue(overview())
    const wrapper = mount(AdminView, { props: { embedded: true, section: 'license' } })
    await flushPromises()
    expect(wrapper.get('[data-report-health]').text()).toContain('never')
    expect(wrapper.get('[data-report-health]').text()).toContain('HTTP 503')
    expect(wrapper.get('[data-report-health]').text()).toContain('Enterprise features stop without renewal')
    expect(wrapper.get('[data-report-warning]').attributes('role')).toBe('alert')
    wrapper.unmount()
  })
  it('refreshes successful recovery and removes the warning', async () => {
    mocks.adminOverview.mockResolvedValueOnce(overview()).mockResolvedValueOnce(overview({
      lastCheckIn: '2026-11-07T13:00:00Z', lastReportError: null, lastReportErrorAt: null,
      featuresStopAt: '2026-12-22T13:00:00Z', reportWarning: false
    }))
    const wrapper = mount(AdminView, { props: { embedded: true, section: 'license' } })
    await flushPromises()
    await wrapper.get('[data-refresh-report]').trigger('click')
    await flushPromises()
    expect(mocks.adminOverview).toHaveBeenCalledTimes(2)
    expect(wrapper.find('[data-report-warning]').exists()).toBe(false)
    expect(wrapper.get('[data-report-health]').text()).not.toContain('HTTP 503')
    expect(wrapper.get('[data-report-health]').text()).toContain('none')
    wrapper.unmount()
  })
})
