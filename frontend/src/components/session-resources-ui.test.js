// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import DuplicateSessionDialog from './DuplicateSessionDialog.vue'
import SessionsView from './SessionsView.vue'
import { canApplyNow, resourcePayload, resourcesChanged, resourceSummary } from '../lib/resources.js'

// Per-session CPU/memory limits and restarting a running session to apply them (#215).
const mocks = vi.hoisted(() => ({
  api: {
    createSession: vi.fn(), updateSession: vi.fn(), duplicateSession: vi.fn(), applySessionResources: vi.fn(),
    getCredentialStatus: vi.fn(), getAllowedAgents: vi.fn(), mcpServers: vi.fn(), listProviderAccounts: vi.fn(),
    listSecrets: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const mountOptions = { global: { stubs: { RepoPicker: { template: '<div data-repo-picker></div>' } } } }
const running = {
  id: 's1', title: 'Existing', mode: 'Interactive', phase: 'Running', agent: 'Claude', authMode: 'Subscription',
  policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }, repos: [],
  cpu: '500m', memory: '1Gi', cpuLimit: null, memoryLimit: null,
  resources: { cpuRequest: '500m', cpuLimit: '2', memoryRequest: '1Gi', memoryLimit: '4Gi' }
}

async function openAdvanced(wrapper) {
  await flushPromises()
  await wrapper.get('[data-advanced]').trigger('click')
}

describe('session resources', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({})
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude'] })
    mocks.api.mcpServers.mockResolvedValue([])
    mocks.api.listProviderAccounts.mockResolvedValue({})
    mocks.api.listSecrets.mockResolvedValue([])
    mocks.api.createSession.mockResolvedValue({ id: 'new' })
    mocks.api.updateSession.mockResolvedValue({ id: 's1', phase: 'Running' })
    mocks.api.applySessionResources.mockResolvedValue({ id: 's1', phase: 'Pending' })
    mocks.api.duplicateSession.mockResolvedValue({ id: 'copy' })
  })

  it('helpers send an empty limit for the default and compare against the stored values', () => {
    expect(resourcePayload({ cpu: ' 1 ', memory: '2Gi', cpuLimit: '', memoryLimit: ' 8Gi ' }))
      .toEqual({ cpu: '1', memory: '2Gi', cpuLimit: '', memoryLimit: '8Gi' })
    expect(resourcesChanged({ cpu: '500m', memory: '1Gi', cpuLimit: '', memoryLimit: '' }, running)).toBe(false)
    expect(resourcesChanged({ cpu: '500m', memory: '1Gi', cpuLimit: '4', memoryLimit: '' }, running)).toBe(true)
    expect(canApplyNow(running)).toBe(true)
    expect(canApplyNow({ ...running, mode: 'Autonomous' })).toBe(false)
    expect(canApplyNow({ ...running, phase: 'Paused' })).toBe(false)
    expect(resourceSummary(running.resources)).toBe('CPU 500m / 2 · memory 1Gi / 4Gi')
  })

  it('creates with limits, and without them leaves the instance default', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    const adv = wrapper.find('[data-advanced]')
    if (adv.exists()) await adv.trigger('click')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSession.mock.calls[0][0]).toMatchObject({ cpu: '500m', memory: '1Gi', cpuLimit: '', memoryLimit: '' })

    mocks.api.createSession.mockClear()
    await wrapper.get('[data-resource="cpuLimit"]').setValue('4')
    await wrapper.get('[data-resource="memoryLimit"]').setValue('8Gi')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSession.mock.calls[0][0]).toMatchObject({ cpuLimit: '4', memoryLimit: '8Gi' })
  })

  it('shows the effective values and the default limit of the edited session', async () => {
    const wrapper = mount(EditSessionDialog, { props: { session: running, projects: [] }, ...mountOptions })
    await openAdvanced(wrapper)
    expect(wrapper.get('[data-session-resources-effective]').text()).toContain('CPU 500m / 2 · memory 1Gi / 4Gi')
    expect(wrapper.get('[data-resource="cpuLimit"]').attributes('placeholder')).toBe('default (2)')
    expect(wrapper.find('[data-resources-restart]').exists()).toBe(false)
  })

  it('saves and then restarts a running interactive session when its resources change', async () => {
    const wrapper = mount(EditSessionDialog, { props: { session: running, projects: [] }, ...mountOptions })
    await openAdvanced(wrapper)
    await wrapper.get('[data-resource="memoryLimit"]').setValue('8Gi')
    expect(wrapper.get('[data-resources-restart]').text()).toContain('conversation continues')
    expect(wrapper.get('[data-submit]').text()).toBe('Save and restart')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSession.mock.calls[0][1]).toMatchObject({ memoryLimit: '8Gi', cpuLimit: '' })
    expect(mocks.api.applySessionResources).toHaveBeenCalledWith('s1')
    expect(mocks.api.updateSession.mock.invocationCallOrder[0]).toBeLessThan(mocks.api.applySessionResources.mock.invocationCallOrder[0])
    expect(wrapper.emitted('updated')[0][0]).toMatchObject({ phase: 'Pending' })
  })

  it('only saves when the owner chooses the next start', async () => {
    const wrapper = mount(EditSessionDialog, { props: { session: running, projects: [] }, ...mountOptions })
    await openAdvanced(wrapper)
    await wrapper.get('[data-resource="cpu"]').setValue('1')
    await wrapper.get('[data-resources-restart-now]').setValue(false)
    expect(wrapper.get('[data-submit]').text()).toBe('Save changes')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSession).toHaveBeenCalled()
    expect(mocks.api.applySessionResources).not.toHaveBeenCalled()
  })

  it('keeps the dialog open with the reason when the restart is refused', async () => {
    mocks.api.applySessionResources.mockRejectedValue(new Error('The cluster would not start this session with the new resources, so it keeps running unchanged: maximum memory usage per Container is 6Gi.'))
    const wrapper = mount(EditSessionDialog, { props: { session: running, projects: [] }, ...mountOptions })
    await openAdvanced(wrapper)
    await wrapper.get('[data-resource="memoryLimit"]').setValue('8Gi')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Saved, but the session was not restarted')
    expect(wrapper.text()).toContain('maximum memory usage per Container is 6Gi')
    expect(wrapper.emitted('updated')).toBeUndefined()
  })

  it('offers no restart for an autonomous session and says when the values apply', async () => {
    const wrapper = mount(EditSessionDialog, { props: { session: { ...running, mode: 'Autonomous' }, projects: [] }, ...mountOptions })
    await openAdvanced(wrapper)
    await wrapper.get('[data-resource="cpu"]').setValue('1')
    expect(wrapper.find('[data-resources-restart]').exists()).toBe(false)
    expect(wrapper.get('[data-resources-next-start]').text()).toContain('next start')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.applySessionResources).not.toHaveBeenCalled()
  })

  it('offers the restart for a pod that still runs on older values', async () => {
    const wrapper = mount(EditSessionDialog, { props: { session: { ...running, resourcesPendingRestart: true }, projects: [] }, ...mountOptions })
    await openAdvanced(wrapper)
    expect(wrapper.find('[data-resources-restart]').exists()).toBe(true)
    expect(wrapper.get('[data-session-resources-effective]').text()).toContain('previous values')
  })

  it('duplicates with the source resources prefilled and edited', async () => {
    const wrapper = mount(DuplicateSessionDialog, { props: { session: { ...running, cpuLimit: '4' }, projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.get('[data-resource="cpuLimit"]').element.value).toBe('4')
    await wrapper.get('[data-resource="memory"]').setValue('2Gi')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.duplicateSession.mock.calls[0][1]).toMatchObject({ cpu: '500m', memory: '2Gi', cpuLimit: '4', memoryLimit: '' })
  })

  it('lists the effective values in the Sessions overview, and when they still wait for a restart', async () => {
    const wrapper = mount(SessionsView, { props: { sessions: [running, { ...running, id: 's2', resourcesPendingRestart: true }], projects: [], query: '' } })
    const rows = wrapper.findAll('[data-session-resources]')
    expect(rows.map(r => r.text())).toEqual(['CPU 500m / 2 · memory 1Gi / 4Gi', 'CPU 500m / 2 · memory 1Gi / 4Gi · applies at the next restart'])
  })
})
