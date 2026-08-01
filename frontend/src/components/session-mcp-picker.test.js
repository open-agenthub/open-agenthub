// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import DuplicateSessionDialog from './DuplicateSessionDialog.vue'

const mocks = vi.hoisted(() => ({
  api: {
    createSession: vi.fn(), updateSession: vi.fn(), duplicateSession: vi.fn(),
    getCredentialStatus: vi.fn(), getAllowedAgents: vi.fn(), mcpServers: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const RepoPickerStub = { template: '<div data-repo-picker></div>' }
const mountOptions = { global: { stubs: { RepoPicker: RepoPickerStub } } }

const servers = [
  { id: 'm1', name: 'docs-search', owner: 'me', mine: true },
  { id: 'm2', name: 'ticket-api', owner: 'alice', mine: false },
  { id: 'm3', name: 'org-docs', owner: '__org__', mine: false }
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
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw'] })
    mocks.api.mcpServers.mockResolvedValue(servers)
    mocks.api.createSession.mockResolvedValue({ id: 'new' })
    mocks.api.updateSession.mockResolvedValue({ id: 's1' })
    mocks.api.duplicateSession.mockResolvedValue({ id: 'copy' })
  })

  it('sends the selected ids from the New session dialog', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    const options = wrapper.findAll('[data-mcp-option]')
    expect(options).toHaveLength(3)
    expect(wrapper.get('[data-mcp-badge="m1"]').text()).toBe('Mine')
    expect(wrapper.get('[data-mcp-badge="m2"]').text()).toBe('Shared')
    expect(wrapper.get('[data-mcp-badge="m3"]').text()).toBe('Org')
    await options[0].setValue(true)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      mcpServerIds: ['m1'],
      ephemeralApiSources: []
    }))
  })

  it('sends an empty id list when nothing is selected', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      mcpServerIds: [],
      ephemeralApiSources: []
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
    expect(options).toHaveLength(3)
    expect(options.find(o => o.element.value === 'm2').element.checked).toBe(true)
    expect(options.find(o => o.element.value === 'm1').element.checked).toBe(false)
    await options.find(o => o.element.value === 'm1').setValue(true)
    await wrapper.get('[data-submit]').trigger('click')
    const payload = mocks.api.updateSession.mock.calls[0][1]
    expect([...payload.mcpServerIds].sort()).toEqual(['m1', 'm2'])
  })

  it('prefills the duplicate dialog from the source session and sends adjusted ids', async () => {
    const wrapper = mount(DuplicateSessionDialog, {
      props: { session: { ...baseSession, mcpServerIds: ['m2'] }, projects: [] },
      ...mountOptions
    })
    await flushPromises()
    const options = wrapper.findAll('[data-mcp-option]')
    expect(options).toHaveLength(3)
    expect(options.find(o => o.element.value === 'm2').element.checked).toBe(true)
    expect(options.find(o => o.element.value === 'm1').element.checked).toBe(false)
    await options.find(o => o.element.value === 'm1').setValue(true)
    await options.find(o => o.element.value === 'm2').setValue(false)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      mcpServerIds: ['m1']
    }))
  })

  it('keeps the duplicate picker independent from the includeMcp checkbox', async () => {
    const wrapper = mount(DuplicateSessionDialog, {
      props: { session: { ...baseSession, mcpServerIds: ['m1'] }, projects: [] },
      ...mountOptions
    })
    await flushPromises()
    await wrapper.get('.check input[type="checkbox"]').setValue(false) // includeMcp off
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      includeMcp: false,
      mcpServerIds: ['m1'] // picker selection still sent
    }))
  })

  it('renders no duplicate picker when the library is empty', async () => {
    mocks.api.mcpServers.mockResolvedValue([])
    const wrapper = mount(DuplicateSessionDialog, {
      props: { session: baseSession, projects: [] }, ...mountOptions
    })
    await flushPromises()
    expect(wrapper.find('[data-mcp-picker]').exists()).toBe(false)
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

describe('ephemeral API URL paste', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({})
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw'] })
    mocks.api.mcpServers.mockResolvedValue(servers)
    mocks.api.createSession.mockResolvedValue({ id: 'new' })
    mocks.api.updateSession.mockResolvedValue({ id: 's1' })
  })

  it('derives the ephemeral name from the URL hostname on create', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-ephemeral-url]').setValue('https://api.example.com/openapi.json')
    await flushPromises()
    expect(wrapper.get('[data-ephemeral-name]').element.value).toBe('api-example-com')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      ephemeralApiSources: [{
        name: 'api-example-com',
        specUrl: 'https://api.example.com/openapi.json',
        specType: 'auto',
        saveToLibrary: false
      }]
    }))
  })

  it('lets the user override the name and save to the library', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-ephemeral-url]').setValue('https://petstore.example.test/openapi.json')
    await flushPromises()
    await wrapper.get('[data-ephemeral-name]').setValue('pets')
    await wrapper.get('[data-ephemeral-save]').setValue(true)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      ephemeralApiSources: [{
        name: 'pets',
        specUrl: 'https://petstore.example.test/openapi.json',
        specType: 'auto',
        saveToLibrary: true
      }]
    }))
  })

  it('sends empty ephemeralApiSources when the URL is blank', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-ephemeral-url]').setValue('   ')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      ephemeralApiSources: []
    }))
  })

  it('sends ephemeralApiSources from the edit dialog', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: baseSession, projects: [] },
      ...mountOptions
    })
    await flushPromises()
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-ephemeral-url]').setValue('https://books.example.com/schema.graphql')
    await flushPromises()
    await wrapper.get('[data-ephemeral-save]').setValue(true)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      mcpServerIds: [],
      ephemeralApiSources: [{
        name: 'books-example-com',
        specUrl: 'https://books.example.com/schema.graphql',
        specType: 'auto',
        saveToLibrary: true
      }]
    }))
  })
})
