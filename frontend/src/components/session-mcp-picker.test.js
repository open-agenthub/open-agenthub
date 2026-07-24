// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'

const mocks = vi.hoisted(() => ({
  api: {
    createSession: vi.fn(), updateSession: vi.fn(),
    getCredentialStatus: vi.fn(), mcpServers: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const RepoPickerStub = { template: '<div data-repo-picker></div>' }
const mountOptions = { global: { stubs: { RepoPicker: RepoPickerStub } } }

const servers = [
  { id: 'm1', name: 'docs-search', owner: 'me', mine: true },
  { id: 'm2', name: 'ticket-api', owner: 'alice', mine: false }
]
const baseSession = {
  id: 's1', title: 'Existing', mode: 'Autonomous', agent: 'Claude', authMode: 'Subscription',
  policy: { allowedTools: ['Read'], allowedMcpTools: [], allowedCommands: [] },
  repos: [], cpu: '500m', memory: '1Gi'
}

describe('saved MCP server picker', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({})
    mocks.api.mcpServers.mockResolvedValue(servers)
    mocks.api.createSession.mockResolvedValue({ id: 'new' })
    mocks.api.updateSession.mockResolvedValue({ id: 's1' })
  })

  it('sends the selected ids from the New session dialog', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    const options = wrapper.findAll('[data-mcp-option]')
    expect(options).toHaveLength(2)
    expect(wrapper.get('[data-mcp-picker]').text()).toContain('shared by alice')
    await options[0].setValue(true)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      mcpServerIds: ['m1']
    }))
  })

  it('sends an empty id list when nothing is selected', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      mcpServerIds: []
    }))
  })

  it('renders no picker when the library is empty', async () => {
    mocks.api.mcpServers.mockResolvedValue([])
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.find('[data-mcp-picker]').exists()).toBe(false)
  })

  it('survives an older backend without the library endpoint', async () => {
    mocks.api.mcpServers.mockRejectedValue(new Error('404 not found'))
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.find('[data-mcp-picker]').exists()).toBe(false)
    expect(wrapper.find('textarea[placeholder*="mcpServers"]').exists()).toBe(true)
  })

  it('prefills checked boxes from the session and sends updated ids on edit', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: { ...baseSession, mcpServerIds: ['m2'] }, projects: [] },
      ...mountOptions
    })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    const options = wrapper.findAll('[data-mcp-option]')
    expect(options).toHaveLength(2)
    expect(options.find(o => o.element.value === 'm2').element.checked).toBe(true)
    expect(options.find(o => o.element.value === 'm1').element.checked).toBe(false)
    await options.find(o => o.element.value === 'm1').setValue(true)
    await wrapper.get('[data-submit]').trigger('click')
    const payload = mocks.api.updateSession.mock.calls[0][1]
    expect([...payload.mcpServerIds].sort()).toEqual(['m1', 'm2'])
  })

  it('sends an empty list when the last saved server is unchecked on edit', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: { ...baseSession, mcpServerIds: ['m1'] }, projects: [] },
      ...mountOptions
    })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.findAll('[data-mcp-option]').find(o => o.element.value === 'm1').setValue(false)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession.mock.calls[0][1].mcpServerIds).toEqual([])
  })
})
