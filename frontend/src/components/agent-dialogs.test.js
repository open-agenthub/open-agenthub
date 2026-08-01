// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import DuplicateSessionDialog from './DuplicateSessionDialog.vue'
import CredentialsDialog from './CredentialsDialog.vue'

const mocks = vi.hoisted(() => ({
  api: {
    createSession: vi.fn(), updateSession: vi.fn(), duplicateSession: vi.fn(),
    getCredentialStatus: vi.fn(), storeCredentials: vi.fn(),
    getAllowedAgents: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const RepoPickerStub = { template: '<div data-repo-picker></div>' }
const mountOptions = { global: { stubs: { RepoPicker: RepoPickerStub } } }
const baseSession = {
  id: 's1', title: 'Existing', mode: 'Autonomous', agent: 'Claude', authMode: 'Subscription',
  policy: { allowedTools: ['Read'], allowedMcpTools: [], allowedCommands: [] },
  repos: [], cpu: '500m', memory: '1Gi'
}

describe('agent-aware session dialogs', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({})
    mocks.api.createSession.mockResolvedValue({ id: 'new' })
    mocks.api.updateSession.mockResolvedValue({ id: 's1' })
    mocks.api.duplicateSession.mockResolvedValue({ id: 'copy' })
    mocks.api.storeCredentials.mockResolvedValue(null)
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw'] })
  })

  it('hides disallowed agents in New Session', async () => {
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex'] })
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    expect(wrapper.find('[data-agent-option="Claude"]').exists()).toBe(true)
    expect(wrapper.find('[data-agent-option="Codex"]').exists()).toBe(true)
    expect(wrapper.find('[data-agent-option="Cursor"]').exists()).toBe(false)
    expect(wrapper.find('[data-agent-option="OpenClaw"]').exists()).toBe(false)
  })

  it('shows the API 403 message when creating with a disallowed agent', async () => {
    const err = new Error("403 Agent 'OpenClaw' is not allowed on this instance. Ask an administrator to enable it, or choose a different agent.")
    err.status = 403
    mocks.api.createSession.mockRejectedValue(err)
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    await wrapper.get('[data-agent-option="OpenClaw"]').trigger('click')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(wrapper.get('.err').text()).toContain("Agent 'OpenClaw' is not allowed")
  })

  it('creates a Codex API-key autonomous session with an exact structured policy', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    await wrapper.get('[data-auth-option="ApiKey"]').trigger('click')
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-policy="allowedTools"]').setValue('Read\nEdit')
    await wrapper.get('[data-policy="allowedMcpTools"]').setValue('')
    await wrapper.get('[data-policy="allowedCommands"]').setValue('git status')
    await wrapper.get('[data-submit]').trigger('click')

    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      agent: 'Codex', authMode: 'ApiKey',
      policy: { allowedTools: ['Read', 'Edit'], allowedMcpTools: [], allowedCommands: ['git status'] }
    }))
    expect(mocks.api.createSession.mock.calls[0][0]).not.toHaveProperty('allowedTools')
  })

  it('creates a Cursor API-key autonomous session', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-agent-option="Cursor"]').trigger('click')
    await wrapper.get('[data-auth-option="ApiKey"]').trigger('click')
    expect(wrapper.find('[data-auth-option="Auto"]').exists()).toBe(false)
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').attributes('placeholder')).toContain('Shell(')
    expect(wrapper.get('[data-policy="allowedTools"]').attributes('placeholder')).toContain('Read(**)')
    expect(wrapper.find('[data-policy="allowedCommands"]').exists()).toBe(false)
    await wrapper.get('[data-submit]').trigger('click')

    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      agent: 'Cursor', authMode: 'ApiKey',
      policy: expect.objectContaining({ allowedCommands: [] })
    }))
  })

  it('creates an OpenClaw API-key session with a selected key source', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-agent-option="OpenClaw"]').trigger('click')
    await wrapper.get('[data-auth-option="ApiKey"]').trigger('click')
    expect(wrapper.find('[data-auth-option="Auto"]').exists()).toBe(false)
    expect(wrapper.find('[data-openclaw-source]').exists()).toBe(true)
    await wrapper.get('[data-openclaw-source-option="OpenAI"]').trigger('click')
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-submit]').trigger('click')

    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      agent: 'OpenClaw', authMode: 'ApiKey', openClawApiKeySource: 'OpenAI'
    }))
  })

  it('omits openClawApiKeySource for OpenClaw subscription and other agents', async () => {
    const openClaw = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await openClaw.get('[data-agent-option="OpenClaw"]').trigger('click')
    expect(openClaw.find('[data-openclaw-source]').exists()).toBe(false)
    await openClaw.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0]).not.toHaveProperty('openClawApiKeySource')

    mocks.api.createSession.mockClear()
    const claude = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await claude.get('[data-auth-option="ApiKey"]').trigger('click')
    expect(claude.find('[data-openclaw-source]').exists()).toBe(false)
    await claude.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0]).not.toHaveProperty('openClawApiKeySource')
  })

  it('edits and duplicates OpenClaw ApiKey source selections', async () => {
    const session = {
      ...baseSession,
      agent: 'OpenClaw',
      authMode: 'ApiKey',
      openClawApiKeySource: 'Anthropic'
    }
    const edit = mount(EditSessionDialog, { props: { session, projects: [] }, ...mountOptions })
    expect(edit.find('[data-openclaw-source]').exists()).toBe(true)
    await edit.get('[data-openclaw-source-option="Cursor"]').trigger('click')
    await edit.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      agent: 'OpenClaw', authMode: 'ApiKey', openClawApiKeySource: 'Cursor'
    }))

    const duplicate = mount(DuplicateSessionDialog, { props: { session, projects: [] } })
    expect(duplicate.find('[data-openclaw-source-option="Anthropic"]').classes()).toContain('on')
    await duplicate.get('[data-openclaw-source-option="OpenAI"]').trigger('click')
    await duplicate.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      agent: 'OpenClaw', authMode: 'ApiKey', openClawApiKeySource: 'OpenAI'
    }))
  })

  it('hides shell command prefixes when editing or duplicating a Cursor session', async () => {
    const cursorSession = {
      ...baseSession,
      agent: 'Cursor',
      authMode: 'ApiKey',
      policy: { allowedTools: ['Read(**)'], allowedMcpTools: [], allowedCommands: [] }
    }
    const edit = mount(EditSessionDialog, { props: { session: cursorSession, projects: [] }, ...mountOptions })
    await edit.get('[data-advanced]').trigger('click')
    expect(edit.get('[data-policy="allowedTools"]').attributes('placeholder')).toContain('Write(')
    expect(edit.find('[data-policy="allowedCommands"]').exists()).toBe(false)

    const duplicate = mount(DuplicateSessionDialog, { props: { session: cursorSession, projects: [] } })
    await duplicate.get('[data-advanced]').trigger('click')
    expect(duplicate.find('[data-policy="allowedCommands"]').exists()).toBe(false)
    expect(duplicate.find('[data-auth-option="Auto"]').exists()).toBe(false)
  })

  it('creates Claude automation with native MCP rules and exact shell command semantics', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.previousElementSibling.textContent).toContain('Exact shell commands')
    expect(wrapper.get('[data-claude-command-semantics]').text()).toContain('compound commands are rejected')
    await wrapper.get('[data-policy="allowedMcpTools"]').setValue('mcp__docs__search\nmcp__git__*')
    await wrapper.get('[data-policy="allowedCommands"]').setValue('git status')
    await wrapper.get('[data-submit]').trigger('click')

    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      agent: 'Claude',
      policy: expect.objectContaining({
        allowedMcpTools: ['mcp__docs__search', 'mcp__git__*'],
        allowedCommands: ['git status']
      })
    }))
  })

  it('edits Claude automation with the same exact shell command semantics', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: baseSession, projects: [] },
      ...mountOptions
    })
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.previousElementSibling.textContent).toContain('Exact shell commands')
    expect(wrapper.get('[data-claude-command-semantics]').text()).toContain('compound commands are rejected')
    await wrapper.get('[data-policy="allowedMcpTools"]').setValue('mcp__docs__search')
    await wrapper.get('[data-policy="allowedCommands"]').setValue('npm test')
    await wrapper.get('[data-submit]').trigger('click')

    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      policy: {
        allowedTools: ['Read'],
        allowedMcpTools: ['mcp__docs__search'],
        allowedCommands: ['npm test']
      }
    }))
  })

  it('duplicates Claude automation with the same exact shell command semantics', async () => {
    const wrapper = mount(DuplicateSessionDialog, {
      props: { session: baseSession, projects: [] }
    })
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.previousElementSibling.textContent).toContain('Exact shell commands')
    expect(wrapper.get('[data-claude-command-semantics]').text()).toContain('compound commands are rejected')
    await wrapper.get('[data-policy="allowedMcpTools"]').setValue('mcp__docs__search')
    await wrapper.get('[data-policy="allowedCommands"]').setValue('dotnet test')
    await wrapper.get('[data-submit]').trigger('click')

    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      agent: 'Claude',
      policy: {
        allowedTools: ['Read'],
        allowedMcpTools: ['mcp__docs__search'],
        allowedCommands: ['dotnet test']
      }
    }))
  })

  it('changes untouched New-session policy defaults with the selected provider', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).not.toContain('Bash(git*)')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.value).toContain('git status')
  })

  it('resets untouched policy to Cursor defaults when switching from Codex', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).toContain('Read')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).toContain('Edit')

    await wrapper.get('[data-agent-option="Cursor"]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).toContain('Shell(')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).toContain('Read(**)')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).toContain('Write(**)')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).not.toContain('Edit')
    expect(wrapper.find('[data-policy="allowedCommands"]').exists()).toBe(false)
  })

  it('hides automation policy in Interactive but retains it across mode toggles', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-policy="allowedCommands"]').setValue('npm test')
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Interactive').trigger('click')
    expect(wrapper.find('[data-policy="allowedCommands"]').exists()).toBe(false)
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.value).toBe('npm test')
  })

  it('edits agent, auth, and policy while exposing legacy Auto only on migrated sessions', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: { ...baseSession, authMode: 'Auto', allowedTools: ['Read'], policy: null }, projects: [] },
      ...mountOptions
    })
    expect(wrapper.find('[data-auth-option="Auto"]').exists()).toBe(true)
    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    expect(wrapper.find('[data-auth-option="Auto"]').exists()).toBe(false)
    await wrapper.get('[data-auth-option="ApiKey"]').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-policy="allowedCommands"]').setValue('dotnet test')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      agent: 'Codex', authMode: 'ApiKey',
      policy: { allowedTools: ['Read'], allowedMcpTools: [], allowedCommands: ['dotnet test'] }
    }))
  })

  it('treats migrated Auto as read-only and omits it from an unchanged edit payload', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: { ...baseSession, mode: 'Interactive', authMode: 'Auto' }, projects: [] },
      ...mountOptions
    })
    expect(wrapper.get('[data-auth-option="Auto"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-submit]').trigger('click')
    const payload = mocks.api.updateSession.mock.calls[0][1]
    expect(payload).not.toHaveProperty('authMode')
    expect(payload).not.toHaveProperty('agent')
  })

  it('resubmits an explicitly empty automated Edit policy as default deny', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: { ...baseSession, policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] } }, projects: [] },
      ...mountOptions
    })
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).toBe('')
    expect(wrapper.get('[data-policy="allowedMcpTools"]').element.value).toBe('')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.value).toBe('')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession.mock.calls[0][1].policy).toEqual({
      allowedTools: [], allowedMcpTools: [], allowedCommands: []
    })
  })

  it('keeps scheduled edit restrictions honest by hiding ignored runtime controls', () => {
    const wrapper = mount(EditSessionDialog, { props: { session: { ...baseSession, mode: 'Scheduled' }, projects: [] }, ...mountOptions })
    expect(wrapper.find('[data-agent-card]').exists()).toBe(false)
    expect(wrapper.text()).toContain('delete and recreate')
  })

  it('duplicates with explicit agent, auth, and structured policy selections', async () => {
    const wrapper = mount(DuplicateSessionDialog, { props: { session: baseSession, projects: [] } })
    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    await wrapper.get('[data-auth-option="ApiKey"]').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    await wrapper.get('[data-policy="allowedCommands"]').setValue('npm test')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({
      agent: 'Codex', authMode: 'ApiKey',
      policy: { allowedTools: ['Read'], allowedMcpTools: [], allowedCommands: ['npm test'] }
    }))
  })

  it('shows legacy Auto when duplicating a migrated Claude session', () => {
    const wrapper = mount(DuplicateSessionDialog, {
      props: { session: { ...baseSession, authMode: 'Auto' }, projects: [] }
    })
    expect(wrapper.find('[data-auth-option="Auto"]').exists()).toBe(true)
  })

  it('resubmits an explicitly empty automated Duplicate policy as default deny', async () => {
    const wrapper = mount(DuplicateSessionDialog, {
      props: { session: { ...baseSession, policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] } }, projects: [] }
    })
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).toBe('')
    expect(wrapper.get('[data-policy="allowedMcpTools"]').element.value).toBe('')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.value).toBe('')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession.mock.calls[0][1].policy).toEqual({
      allowedTools: [], allowedMcpTools: [], allowedCommands: []
    })
  })

  it('shows non-blocking Interactive subscription login guidance and automation readiness', async () => {
    const interactive = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await flushPromises()
    expect(interactive.get('[data-readiness]').text()).toContain('start now and sign in')
    expect(interactive.get('[data-submit]').attributes('disabled')).toBeUndefined()

    mocks.api.getCredentialStatus.mockResolvedValue({ openAiApiKey: true })
    const autonomous = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await autonomous.get('[data-agent-option="Codex"]').trigger('click')
    await autonomous.get('[data-auth-option="ApiKey"]').trigger('click')
    await autonomous.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await flushPromises()
    expect(autonomous.get('[data-readiness]').text()).toContain('OpenAI API key is stored')
  })
})

describe('OpenAI credentials', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({ openAiApiKey: true })
    mocks.api.storeCredentials.mockResolvedValue(null)
  })

  it('renders a write-only password field and clears the stored OpenAI key without readback', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    const input = wrapper.get('[data-credential="openAiApiKey"]')
    expect(input.attributes('type')).toBe('password')
    expect(input.element.value).toBe('')
    expect(wrapper.text()).not.toContain('sk-secret')
    await wrapper.get('[data-clear="openAiApiKey"]').trigger('click')
    await wrapper.get('[data-save-credentials]').trigger('click')
    expect(mocks.api.storeCredentials).toHaveBeenCalledWith({ clear: ['openAiApiKey'] })
  })
})

describe('Cursor credentials', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({ cursorApiKey: true, cursorSubscription: true })
    mocks.api.storeCredentials.mockResolvedValue(null)
  })

  it('clears Cursor API key without reading it back', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    const input = wrapper.get('[data-credential="cursorApiKey"]')
    expect(input.attributes('type')).toBe('password')
    expect(input.element.value).toBe('')
    expect(wrapper.find('[data-credential-status="cursorApiKey"]').exists()).toBe(true)
    expect(wrapper.find('[data-credential-status="cursorSubscription"]').exists()).toBe(true)
    await wrapper.get('[data-clear="cursorApiKey"]').trigger('click')
    await wrapper.get('[data-save-credentials]').trigger('click')
    expect(mocks.api.storeCredentials).toHaveBeenCalledWith({ clear: ['cursorApiKey'] })
  })
})

describe('OpenClaw credentials', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({ openclawSubscription: true })
    mocks.api.storeCredentials.mockResolvedValue(null)
  })

  it('shows OpenClaw subscription status without a dedicated API key field', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    expect(wrapper.find('[data-credential-status="openclawSubscription"]').exists()).toBe(true)
    expect(wrapper.find('[data-credential-status="openclawSubscription"]').text()).toContain('OpenClaw subscription')
    expect(wrapper.find('[data-credential="openclawApiKey"]').exists()).toBe(false)
    expect(wrapper.text()).not.toMatch(/OpenClaw API key/i)
  })

  it('notes that Anthropic, OpenAI, and Cursor keys can be reused by OpenClaw', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    expect(wrapper.get('[data-credential-hint="anthropicApiKey"]').text()).toContain('OpenClaw')
    expect(wrapper.get('[data-credential-hint="openAiApiKey"]').text()).toContain('OpenClaw')
    expect(wrapper.get('[data-credential-hint="cursorApiKey"]').text()).toContain('OpenClaw')
    expect(wrapper.get('[data-credential-hint="openAiApiKey"]').text()).not.toMatch(/only when a Codex/i)
    expect(wrapper.get('[data-credential-hint="cursorApiKey"]').text()).not.toMatch(/only when a Cursor/i)
  })
})
