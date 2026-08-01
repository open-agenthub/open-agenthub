// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import McpServersPane from './McpServersPane.vue'
import SkillsPane from './SkillsPane.vue'

const mocks = vi.hoisted(() => ({
  api: {
    mcpServers: vi.fn(), createMcpServer: vi.fn(), updateMcpServer: vi.fn(), deleteMcpServer: vi.fn(),
    skills: vi.fn(), skill: vi.fn(), createSkill: vi.fn(), updateSkill: vi.fn(), deleteSkill: vi.fn(),
    searchSkills: vi.fn(), skillVersions: vi.fn(), skillVersion: vi.fn(), restoreSkillVersion: vi.fn(),
    listProjects: vi.fn(),
    libraryGroups: vi.fn(), librarySettings: vi.fn(), libraryShares: vi.fn(), setLibraryShares: vi.fn(),
    libraryUsers: vi.fn()
  }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const err402 = () => Object.assign(new Error('402 license required'), { status: 402 })

const servers = [
  { id: 'm1', name: 'docs-search', description: 'Company docs', owner: 'me', mine: true, configJson: '{"type":"http","url":"https://docs.example/sse"}' },
  { id: 'm2', name: 'ticket-api', description: '', owner: 'alice', mine: false, configJson: null }
]

describe('McpServersPane', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.mcpServers.mockResolvedValue(servers)
    mocks.api.createMcpServer.mockResolvedValue({ id: 'new' })
    mocks.api.libraryGroups.mockResolvedValue([])
    mocks.api.libraryShares.mockResolvedValue({ all: false, users: [], groups: [] })
    mocks.api.setLibraryShares.mockResolvedValue({ all: true, users: [], groups: [] })
    mocks.api.libraryUsers.mockResolvedValue([
      { owner: 'bob', displayName: 'Bob B', email: 'bob@example.dev' },
      { owner: 'carol', displayName: 'Carol C', email: 'carol@example.dev' }
    ])
  })

  it('lists own entries as editable and shared entries read-only with owner pill', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    const rows = wrapper.findAll('[data-mcp-row]')
    expect(rows).toHaveLength(2)
    const own = rows.find(r => r.text().includes('docs-search'))
    const shared = rows.find(r => r.text().includes('ticket-api'))
    expect(own.find('[data-mcp-edit]').exists()).toBe(true)
    expect(own.find('[data-mcp-delete]').exists()).toBe(true)
    expect(own.find('[data-mcp-shared]').exists()).toBe(false)
    expect(shared.find('[data-mcp-edit]').exists()).toBe(false)
    expect(shared.find('[data-mcp-delete]').exists()).toBe(false)
    expect(shared.get('[data-mcp-shared]').text()).toContain('alice')
  })

  it('creates a server through the inline form', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
    await wrapper.get('[data-mcp-name]').setValue('my-server')
    await wrapper.get('[data-mcp-desc]').setValue('Test')
    await wrapper.get('[data-mcp-config]').setValue('{"type":"http","url":"https://x/sse"}')
    await wrapper.get('[data-mcp-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.createMcpServer).toHaveBeenCalledWith({
      name: 'my-server', description: 'Test', configJson: '{"type":"http","url":"https://x/sse"}'
    })
    expect(wrapper.find('[data-mcp-form]').exists()).toBe(false)
  })

  it('shows an inline error for invalid JSON and does not call the API', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-mcp-add]').trigger('click')
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
    await wrapper.get('[data-mcp-name]').setValue('array')
    await wrapper.get('[data-mcp-config]').setValue('[1,2]')
    await wrapper.get('[data-mcp-save]').trigger('click')
    expect(wrapper.get('[data-mcp-form-error]').text()).toContain('single JSON object')
    expect(mocks.api.createMcpServer).not.toHaveBeenCalled()
  })

  it('shows sharing controls to admins on own items only', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: true } })
    await flushPromises()
    expect(wrapper.findAll('[data-mcp-share-toggle]')).toHaveLength(1) // own item only
    await wrapper.get('[data-mcp-share-toggle]').trigger('click')
    await flushPromises()
    const controls = wrapper.get('[data-mcp-share-controls]')
    expect(mocks.api.libraryShares).toHaveBeenCalledWith('mcp-servers', 'm1')
    await controls.get('[data-share-all]').setValue(true)
    // Pick users through the multi-select suggestions.
    const userInput = controls.get('[data-share-users] input')
    await userInput.setValue('bob')
    await controls.get('[data-user-suggestion]').trigger('click')
    await userInput.setValue('carol')
    await controls.get('[data-user-suggestion]').trigger('click')
    await controls.get('[data-share-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.setLibraryShares).toHaveBeenCalledWith('mcp-servers', 'm1', {
      all: true, users: ['bob', 'carol'], groups: []
    })
  })

  it('falls back to a plain user input when the user list is unavailable', async () => {
    mocks.api.libraryUsers.mockRejectedValue(new Error('403 admin only'))
    const wrapper = mount(McpServersPane, { props: { isAdmin: true } })
    await flushPromises()
    await wrapper.get('[data-mcp-share-toggle]').trigger('click')
    await flushPromises()
    const controls = wrapper.get('[data-mcp-share-controls]')
    await controls.get('[data-share-users-fallback]').setValue('bob, carol')
    await controls.get('[data-share-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.setLibraryShares).toHaveBeenCalledWith('mcp-servers', 'm1', {
      all: false, users: ['bob', 'carol'], groups: []
    })
  })

  it('hides sharing controls from non-admins', async () => {
    const wrapper = mount(McpServersPane, { props: { isAdmin: false } })
    await flushPromises()
    expect(wrapper.find('[data-mcp-share-toggle]').exists()).toBe(false)
    expect(wrapper.find('[data-mcp-share-controls]').exists()).toBe(false)
  })

  it('shows a license note instead of controls when sharing answers 402', async () => {
    mocks.api.libraryShares.mockRejectedValue(err402())
    mocks.api.libraryGroups.mockRejectedValue(err402())
    const wrapper = mount(McpServersPane, { props: { isAdmin: true } })
    await flushPromises()
    await wrapper.get('[data-mcp-share-toggle]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-share-locked]').text()).toContain('Enterprise license required')
    expect(wrapper.find('[data-share-save]').exists()).toBe(false)
  })
})

const skills = [
  { id: 'k1', name: 'review-checklist', description: 'PR review steps', owner: 'me', mine: true, version: 2, projectId: null },
  { id: 'k2', name: 'deploy-notes', description: '', owner: 'alice', mine: false, version: 1, projectId: null }
]

describe('SkillsPane', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.skills.mockResolvedValue(skills)
    mocks.api.librarySettings.mockResolvedValue({ userSkillPublishing: true })
    mocks.api.libraryShares.mockResolvedValue({ all: false, users: [], groups: [] })
    mocks.api.setLibraryShares.mockResolvedValue({ all: true, users: [], groups: [] })
    mocks.api.libraryGroups.mockResolvedValue([])
    mocks.api.libraryUsers.mockResolvedValue([])
    mocks.api.createSkill.mockResolvedValue({ id: 'new' })
    mocks.api.listProjects.mockResolvedValue([{ id: 'p1', name: 'Webshop' }])
    mocks.api.searchSkills.mockResolvedValue([])
    mocks.api.skillVersions.mockResolvedValue([])
    mocks.api.skillVersion.mockResolvedValue({ content: '' })
    mocks.api.restoreSkillVersion.mockResolvedValue({})
  })

  it('lists skills with shared rows read-only', async () => {
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    const rows = wrapper.findAll('[data-skill-row]')
    expect(rows).toHaveLength(2)
    const shared = rows.find(r => r.text().includes('deploy-notes'))
    expect(shared.find('[data-skill-edit]').exists()).toBe(false)
    expect(shared.get('[data-skill-shared]').text()).toContain('alice')
  })

  it('shows the publish toggle only on own skills when publishing is enabled', async () => {
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    const toggles = wrapper.findAll('[data-skill-publish]')
    expect(toggles).toHaveLength(1)
    expect(wrapper.findAll('[data-skill-row]').find(r => r.text().includes('review-checklist'))
      .find('[data-skill-publish]').exists()).toBe(true)
    await toggles[0].setValue(true)
    await flushPromises()
    expect(mocks.api.setLibraryShares).toHaveBeenCalledWith('skills', 'k1', { all: true, users: [], groups: [] })
  })

  it('hides the publish toggle when the admin disabled user publishing', async () => {
    mocks.api.librarySettings.mockResolvedValue({ userSkillPublishing: false })
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    expect(wrapper.find('[data-skill-publish]').exists()).toBe(false)
  })

  it('hides the publish toggle without a license (402)', async () => {
    mocks.api.librarySettings.mockRejectedValue(err402())
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    expect(wrapper.find('[data-skill-publish]').exists()).toBe(false)
    expect(wrapper.findAll('[data-skill-row]')).toHaveLength(2) // pane keeps working
  })

  it('hides the publish toggle for admins who use the sharing expander instead', async () => {
    const wrapper = mount(SkillsPane, { props: { isAdmin: true } })
    await flushPromises()
    expect(wrapper.find('[data-skill-publish]').exists()).toBe(false)
    expect(wrapper.findAll('[data-skill-share-toggle]')).toHaveLength(1)
  })

  it('creates a skill with a kebab-case name and template content', async () => {
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-skill-add]').trigger('click')
    expect(wrapper.get('[data-skill-content]').element.value).toContain('---') // frontmatter template
    await wrapper.get('[data-skill-name]').setValue('my-skill')
    await wrapper.get('[data-skill-desc]').setValue('Test skill')
    await wrapper.get('[data-skill-content]').setValue('---\nname: my-skill\n---\nDo things.')
    await wrapper.get('[data-skill-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSkill).toHaveBeenCalledWith({
      name: 'my-skill', description: 'Test skill', content: '---\nname: my-skill\n---\nDo things.',
      comment: '', projectId: null
    })
  })

  it('creates a project-scoped skill when a project is selected', async () => {
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-skill-add]').trigger('click')
    await wrapper.get('[data-skill-name]').setValue('deploy-webshop')
    await wrapper.get('[data-skill-project]').setValue('p1')
    await wrapper.get('[data-skill-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSkill).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'deploy-webshop', projectId: 'p1' }))
  })

  it('searches server-side and shows matching rows only', async () => {
    vi.useFakeTimers()
    try {
      mocks.api.searchSkills.mockResolvedValue([
        { id: 'k1', name: 'review-checklist', score: 0.8 }
      ])
      const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
      await flushPromises()
      await wrapper.get('[data-skill-search]').setValue('review')
      await vi.advanceTimersByTimeAsync(300)
      await flushPromises()
      expect(mocks.api.searchSkills).toHaveBeenCalledWith('review')
      const rows = wrapper.findAll('[data-skill-row]')
      expect(rows).toHaveLength(1)
      expect(rows[0].text()).toContain('review-checklist')

      // Clearing the query restores the full list without another request.
      await wrapper.get('[data-skill-search]').setValue('')
      await vi.advanceTimersByTimeAsync(300)
      await flushPromises()
      expect(wrapper.findAll('[data-skill-row]')).toHaveLength(2)
    } finally { vi.useRealTimers() }
  })

  it('shows the version history and restores an old version', async () => {
    mocks.api.skillVersions.mockResolvedValue([
      { version: 2, name: 'review-checklist', createdBy: 'me', comment: 'tightened', createdAt: '2026-08-01T10:00:00Z' },
      { version: 1, name: 'review-checklist', createdBy: 'me', comment: '', createdAt: '2026-07-01T10:00:00Z' }
    ])
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    const own = wrapper.findAll('[data-skill-row]').find(r => r.text().includes('review-checklist'))
    await own.get('[data-skill-history-toggle]').trigger('click')
    await flushPromises()
    const rows = wrapper.findAll('[data-skill-history-row]')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('tightened')

    // The head version offers no restore button, the old one does.
    expect(rows[0].find('[data-skill-version-restore]').exists()).toBe(false)
    await rows[1].get('[data-skill-version-restore]').trigger('click')
    await flushPromises()
    expect(mocks.api.restoreSkillVersion).toHaveBeenCalledWith('k1', 1)
  })

  it('shows the attached files read-only when editing a skill with scripts', async () => {
    mocks.api.skill.mockResolvedValue({
      id: 'k1', name: 'review-checklist', description: '', content: '# body',
      projectId: null, files: [{ path: 'scripts/check.sh', content: 'true' }]
    })
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    const own = wrapper.findAll('[data-skill-row]').find(r => r.text().includes('review-checklist'))
    await own.get('[data-skill-edit]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-skill-files]').text()).toContain('scripts/check.sh')

    // Saving sends no files payload, so the backend keeps them.
    await wrapper.get('[data-skill-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSkill).toHaveBeenCalledWith('k1',
      expect.not.objectContaining({ files: expect.anything() }))
  })

  it('previews an old version inline', async () => {
    mocks.api.skillVersions.mockResolvedValue([
      { version: 1, name: 'review-checklist', createdBy: 'me', comment: '', createdAt: '2026-07-01T10:00:00Z' }
    ])
    mocks.api.skillVersion.mockResolvedValue({ content: '# old content' })
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    const own = wrapper.findAll('[data-skill-row]').find(r => r.text().includes('review-checklist'))
    await own.get('[data-skill-history-toggle]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-skill-version-view]').trigger('click')
    await flushPromises()
    expect(mocks.api.skillVersion).toHaveBeenCalledWith('k1', 1)
    expect(wrapper.get('[data-skill-version-preview]').text()).toContain('# old content')
  })

  it('rejects non-kebab-case names inline without calling the API', async () => {
    const wrapper = mount(SkillsPane, { props: { isAdmin: false } })
    await flushPromises()
    await wrapper.get('[data-skill-add]').trigger('click')
    await wrapper.get('[data-skill-name]').setValue('My Skill')
    await wrapper.get('[data-skill-save]').trigger('click')
    expect(wrapper.get('[data-skill-form-error]').text()).toContain('kebab-case')
    expect(mocks.api.createSkill).not.toHaveBeenCalled()
  })
})
