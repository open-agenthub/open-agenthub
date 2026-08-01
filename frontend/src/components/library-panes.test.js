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
    deleteMcpServer: vi.fn()
  }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

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

describe('McpServersPane', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.mcpServers.mockResolvedValue(servers)
    mocks.api.createMcpServer.mockResolvedValue({ id: 'new' })
    mocks.api.createMcpServerFromApi.mockResolvedValue({ id: 'new-api' })
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
})
