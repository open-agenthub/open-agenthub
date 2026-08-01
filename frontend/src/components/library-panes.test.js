// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import McpServersPane from './McpServersPane.vue'

const mocks = vi.hoisted(() => ({
  api: {
    mcpServers: vi.fn(),
    createMcpServer: vi.fn(),
    createMcpServerFromApi: vi.fn(),
    updateMcpServer: vi.fn(),
    deleteMcpServer: vi.fn(),
    adminMcpServers: vi.fn(),
    createAdminMcpServer: vi.fn(),
    updateAdminMcpServer: vi.fn(),
    deleteAdminMcpServer: vi.fn(),
    libraryShares: vi.fn(),
    setLibraryShares: vi.fn(),
    eeListGroups: vi.fn(),
    adminOverview: vi.fn()
  }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const err402 = () => Object.assign(new Error('402 license required'), { status: 402 })

const servers = [
  {
    id: 'm1', name: 'docs-search', description: 'Company docs', owner: 'me',
    kind: 'raw', mine: true, hasSecret: false,
    configJson: '{"type":"http","url":"https://docs.example/sse"}'
  },
  {
    id: 'm2', name: 'ticket-api', description: '', owner: 'alice',
    kind: 'api', mine: false, hasSecret: false, configJson: null
  },
  {
    id: 'm3', name: 'petstore', description: 'Pets', owner: 'me',
    kind: 'api', mine: true, hasSecret: true,
    configJson: '{"specType":"openapi","specUrl":"https://pet.example/openapi.json"}'
  }
]

const orgServers = [
  {
    id: 'o1', name: 'org-docs', description: 'Shared docs', owner: '__org__',
    kind: 'raw', mine: true, hasSecret: false,
    configJson: '{"type":"http","url":"https://docs.example/sse"}'
  }
]

describe('McpServersPane', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.mcpServers.mockResolvedValue(servers)
    mocks.api.adminMcpServers.mockResolvedValue(orgServers)
    mocks.api.createMcpServer.mockResolvedValue({ id: 'new' })
    mocks.api.createMcpServerFromApi.mockResolvedValue({ id: 'new-api' })
    mocks.api.createAdminMcpServer.mockResolvedValue({ id: 'new-org' })
    mocks.api.eeListGroups.mockResolvedValue([{ name: 'devs', role: 'user', memberCount: 2 }])
    mocks.api.libraryShares.mockResolvedValue({ all: false, users: [], groups: [] })
    mocks.api.setLibraryShares.mockResolvedValue({ all: true, users: [], groups: [] })
    mocks.api.adminOverview.mockResolvedValue({
      users: [
        { owner: 'bob', displayName: 'Bob B', email: 'bob@example.dev' },
        { owner: 'carol', displayName: 'Carol C', email: 'carol@example.dev' }
      ]
    })
  })

  it('lists own entries as editable and shared entries read-only with owner pill', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    const rows = wrapper.findAll('[data-mcp-row]')
    expect(rows).toHaveLength(3)
    const own = rows.find(r => r.text().includes('docs-search'))
    const shared = rows.find(r => r.text().includes('ticket-api'))
    expect(own.find('[data-mcp-edit]').exists()).toBe(true)
    expect(own.find('[data-mcp-delete]').exists()).toBe(true)
    expect(own.find('[data-mcp-shared]').exists()).toBe(false)
    expect(shared.find('[data-mcp-edit]').exists()).toBe(false)
    expect(shared.find('[data-mcp-delete]').exists()).toBe(false)
    expect(shared.get('[data-mcp-shared]').text()).toContain('alice')
  })

  it('shows kind badge and hasSecret indicator on list rows', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    const raw = wrapper.findAll('[data-mcp-row]').find(r => r.text().includes('docs-search'))
    const api = wrapper.findAll('[data-mcp-row]').find(r => r.text().includes('petstore'))
    expect(raw.get('[data-mcp-kind]').text()).toMatch(/raw/i)
    expect(api.get('[data-mcp-kind]').text()).toMatch(/api/i)
    expect(raw.find('[data-mcp-has-secret]').exists()).toBe(false)
    expect(api.get('[data-mcp-has-secret]').text()).toMatch(/secret/i)
  })

  it('creates a raw server through the Raw tab', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-tab-raw]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('my-server')
    await wrapper.get('[data-mcp-desc]').setValue('Test')
    await wrapper.get('[data-mcp-config]').setValue('{"type":"http","url":"https://x/sse"}')
    await wrapper.get('[data-mcp-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.createMcpServer).toHaveBeenCalledWith({
      name: 'my-server',
      description: 'Test',
      kind: 'raw',
      configJson: '{"type":"http","url":"https://x/sse"}'
    })
    expect(mocks.api.createMcpServerFromApi).not.toHaveBeenCalled()
    expect(wrapper.find('[data-mcp-form]').exists()).toBe(false)
  })

  it('creates an api server through the From API tab', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-tab-api]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('petstore')
    await wrapper.get('[data-mcp-desc]').setValue('Pets')
    await wrapper.get('[data-mcp-spec-url]').setValue('https://api.example.test/openapi.json')
    await wrapper.get('[data-mcp-spec-type]').setValue('openapi')
    await wrapper.get('[data-mcp-base-url]').setValue('https://api.example.test')
    await wrapper.get('[data-mcp-secret]').setValue('tok-123')
    await wrapper.get('[data-mcp-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.createMcpServerFromApi).toHaveBeenCalledWith({
      name: 'petstore',
      description: 'Pets',
      specUrl: 'https://api.example.test/openapi.json',
      specType: 'openapi',
      baseUrl: 'https://api.example.test',
      secret: 'tok-123',
      save: true
    })
    expect(mocks.api.createMcpServer).not.toHaveBeenCalled()
    expect(wrapper.find('[data-mcp-form]').exists()).toBe(false)
  })

  it('shows an inline error for invalid JSON and does not call the API', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-tab-raw]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('broken')
    await wrapper.get('[data-mcp-config]').setValue('{not json')
    await wrapper.get('[data-mcp-save]').trigger('click')
    expect(wrapper.get('[data-mcp-form-error]').text()).toContain('not valid JSON')
    expect(mocks.api.createMcpServer).not.toHaveBeenCalled()
  })

  it('rejects configs that are not a single JSON object', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-tab-raw]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('array')
    await wrapper.get('[data-mcp-config]').setValue('[1,2]')
    await wrapper.get('[data-mcp-save]').trigger('click')
    expect(wrapper.get('[data-mcp-form-error]').text()).toContain('single JSON object')
    expect(mocks.api.createMcpServer).not.toHaveBeenCalled()
  })

  it('requires a spec URL on the From API tab', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-tab-api]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('missing-url')
    await wrapper.get('[data-mcp-save]').trigger('click')
    expect(wrapper.get('[data-mcp-form-error]').text()).toMatch(/spec/i)
    expect(mocks.api.createMcpServerFromApi).not.toHaveBeenCalled()
  })

  it('shows sharing controls on own items and saves all/users/groups', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false, mode: 'personal' } })
    await flushPromises()
    // Own items only (docs-search + petstore); shared ticket-api has no toggle.
    expect(wrapper.findAll('[data-mcp-share-toggle]')).toHaveLength(2)
    const own = wrapper.findAll('[data-mcp-row]').find(r => r.text().includes('docs-search'))
    await own.get('[data-mcp-share-toggle]').trigger('click')
    await flushPromises()
    const controls = wrapper.get('[data-mcp-share-controls]')
    expect(mocks.api.libraryShares).toHaveBeenCalledWith('mcp-servers', 'm1')
    expect(mocks.api.eeListGroups).toHaveBeenCalled()
    await controls.get('[data-share-all]').setValue(true)
    const userInput = controls.get('[data-share-users] input')
    await userInput.setValue('bob')
    await controls.get('[data-user-suggestion]').trigger('click')
    await userInput.setValue('carol')
    await controls.get('[data-user-suggestion]').trigger('click')
    await controls.get('[data-share-group]').setValue(true)
    await controls.get('[data-share-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.setLibraryShares).toHaveBeenCalledWith('mcp-servers', 'm1', {
      all: true, users: ['bob', 'carol'], groups: ['devs']
    })
  })

  it('falls back to a plain user input when the user list is unavailable', async () => {
    mocks.api.adminOverview.mockRejectedValue(new Error('403 admin only'))
    const wrapper = mount(McpServersPane, { props: { isAdmin: true } })
    await flushPromises()
    const own = wrapper.findAll('[data-mcp-row]').find(r => r.text().includes('docs-search'))
    await own.get('[data-mcp-share-toggle]').trigger('click')
    await flushPromises()
    const controls = wrapper.get('[data-mcp-share-controls]')
    await controls.get('[data-share-users-fallback]').setValue('bob, carol')
    await controls.get('[data-share-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.setLibraryShares).toHaveBeenCalledWith('mcp-servers', 'm1', {
      all: false, users: ['bob', 'carol'], groups: []
    })
  })

  it('hides sharing controls on shared (not mine) personal rows', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    const shared = wrapper.findAll('[data-mcp-row]').find(r => r.text().includes('ticket-api'))
    expect(shared.find('[data-mcp-share-toggle]').exists()).toBe(false)
    expect(shared.find('[data-mcp-share-controls]').exists()).toBe(false)
  })

  it('shows a license note instead of controls when sharing answers 402', async () => {
    mocks.api.libraryShares.mockRejectedValue(err402())
    mocks.api.eeListGroups.mockRejectedValue(err402())
    const wrapper = mount(McpServersPane, { props: { isAdmin: true } })
    await flushPromises()
    const own = wrapper.findAll('[data-mcp-row]').find(r => r.text().includes('docs-search'))
    await own.get('[data-mcp-share-toggle]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-share-locked]').text()).toContain('Enterprise license required')
    expect(wrapper.find('[data-share-save]').exists()).toBe(false)
  })
})

describe('McpServersPane org mode', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.adminMcpServers.mockResolvedValue(orgServers)
    mocks.api.createAdminMcpServer.mockResolvedValue({ id: 'new-org' })
    mocks.api.eeListGroups.mockResolvedValue([])
    mocks.api.libraryShares.mockResolvedValue({ all: false, users: [], groups: [] })
    mocks.api.setLibraryShares.mockResolvedValue({ all: true, users: [], groups: [] })
    mocks.api.adminOverview.mockResolvedValue({ users: [] })
  })

  it('loads and creates through the admin org MCP APIs', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: true, mode: 'org' } })
    await flushPromises()
    expect(mocks.api.adminMcpServers).toHaveBeenCalled()
    expect(mocks.api.mcpServers).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Org MCP catalog')
    expect(wrapper.findAll('[data-mcp-row]')).toHaveLength(1)

    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-tab-raw]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('org-raw')
    await wrapper.get('[data-mcp-desc]').setValue('Org entry')
    await wrapper.get('[data-mcp-config]').setValue('{"type":"http","url":"https://org/sse"}')
    await wrapper.get('[data-mcp-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.createAdminMcpServer).toHaveBeenCalledWith({
      name: 'org-raw',
      description: 'Org entry',
      kind: 'raw',
      configJson: '{"type":"http","url":"https://org/sse"}'
    })
    expect(mocks.api.createMcpServer).not.toHaveBeenCalled()
  })

  it('creates an org api entry with configJson via admin create', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: true, mode: 'org' } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-tab-api]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('org-api')
    await wrapper.get('[data-mcp-spec-url]').setValue('https://api.example.test/openapi.json')
    await wrapper.get('[data-mcp-spec-type]').setValue('openapi')
    await wrapper.get('[data-mcp-secret]').setValue('tok-org')
    await wrapper.get('[data-mcp-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.createAdminMcpServer).toHaveBeenCalledWith({
      name: 'org-api',
      description: '',
      kind: 'api',
      configJson: JSON.stringify({
        specType: 'openapi',
        specUrl: 'https://api.example.test/openapi.json'
      }),
      secretJson: JSON.stringify({ token: 'tok-org' })
    })
    expect(mocks.api.createMcpServerFromApi).not.toHaveBeenCalled()
  })

  it('exposes sharing controls on org entries', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: true, mode: 'org' } })
    await flushPromises()
    expect(wrapper.findAll('[data-mcp-share-toggle]')).toHaveLength(1)
    await wrapper.get('[data-mcp-share-toggle]').trigger('click')
    await flushPromises()
    expect(mocks.api.libraryShares).toHaveBeenCalledWith('mcp-servers', 'o1')
    expect(wrapper.find('[data-mcp-share-controls]').exists()).toBe(true)
  })
})
