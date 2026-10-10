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
    deleteSubscriptionCredential: vi.fn(),
    addGitPat: vi.fn(), deleteGitPat: vi.fn(),
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
    mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude', 'Codex', 'Cursor', 'OpenClaw', 'OpenCode'] })
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

  it('creates an OpenCode API-key autonomous session with hub-named tools and commands', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-agent-option="OpenCode"]').trigger('click')
    await wrapper.get('[data-auth-option="ApiKey"]').trigger('click')
    expect(wrapper.find('[data-openclaw-source]').exists()).toBe(false)
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').attributes('placeholder')).toContain('Glob')
    expect(wrapper.find('[data-policy="allowedCommands"]').exists()).toBe(true)
    await wrapper.get('[data-submit]').trigger('click')

    const payload = mocks.api.createSession.mock.calls[0][0]
    expect(payload).toMatchObject({
      agent: 'OpenCode', authMode: 'ApiKey',
      policy: { allowedTools: ['Read', 'Edit', 'Glob', 'Grep'], allowedCommands: ['git status', 'npm test'] }
    })
    expect(payload).not.toHaveProperty('openClawApiKeySource')
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

  it('offers the system prompt in Interactive too and sends it on create', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    // Unlike the task, the standing rules are not tied to automation, so no mode toggle is needed.
    expect(wrapper.find('[data-system-prompt]').exists()).toBe(true)
    await wrapper.get('[data-system-prompt]').setValue('  You review, you do not commit.  ')
    expect(wrapper.get('[data-system-prompt-count]').text()).toMatch(/^34 \/ 20[,.  ]?000$/)
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession).toHaveBeenCalledWith(expect.objectContaining({
      systemPrompt: 'You review, you do not commit.'
    }))
  })

  it('sends no system prompt when the field is left blank on create', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.createSession.mock.calls[0][0].systemPrompt).toBeNull()
  })

  it('prefills the system prompt on edit and sends an empty string to clear it', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: { ...baseSession, systemPrompt: 'be terse' }, projects: [] },
      ...mountOptions
    })
    expect(wrapper.get('[data-system-prompt]').element.value).toBe('be terse')
    await wrapper.get('[data-system-prompt]').setValue('be precise')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ systemPrompt: 'be precise' }))

    mocks.api.updateSession.mockClear()
    await wrapper.get('[data-system-prompt]').setValue('')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.updateSession.mock.calls[0][1].systemPrompt).toBe('')
  })

  it('shows a scheduled session’s system prompt read-only and leaves it out of the payload', async () => {
    const wrapper = mount(EditSessionDialog, {
      props: { session: { ...baseSession, mode: 'Scheduled', systemPrompt: 'be terse' }, projects: [] },
      ...mountOptions
    })
    const field = wrapper.get('[data-system-prompt]')
    expect(field.element.value).toBe('be terse')
    expect(field.attributes('readonly')).toBeDefined()
    expect(wrapper.text()).toContain('Fixed by the CronJob spec')
    await wrapper.get('[data-submit]').trigger('click')
    // The backend rejects the field for scheduled sessions; sending it would turn a rename into a 400.
    expect(mocks.api.updateSession.mock.calls[0][1]).not.toHaveProperty('systemPrompt')
  })

  it('carries the system prompt into a duplicate and lets it be edited first', async () => {
    const wrapper = mount(DuplicateSessionDialog, {
      props: { session: { ...baseSession, systemPrompt: 'be terse' }, projects: [] }
    })
    expect(wrapper.get('[data-system-prompt]').element.value).toBe('be terse')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ systemPrompt: 'be terse' }))

    mocks.api.duplicateSession.mockClear()
    await wrapper.get('[data-system-prompt]').setValue('new rules')
    await wrapper.get('[data-submit]').trigger('click')
    expect(mocks.api.duplicateSession).toHaveBeenCalledWith('s1', expect.objectContaining({ systemPrompt: 'new rules' }))
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

describe('Git credentials', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({
      sshPrivateKey: true, gitKnownHosts: true, gitUserName: true, gitUserEmail: true
    })
    mocks.api.storeCredentials.mockResolvedValue(null)
  })

  it('removes a stored SSH key via the clear control', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    const chip = wrapper.get('[data-clear="sshPrivateKey"]')
    expect(chip.text()).toContain('stored')
    await chip.trigger('click')
    expect(wrapper.get('[data-clear="sshPrivateKey"]').text()).toContain('remove')
    await wrapper.get('[data-save-credentials]').trigger('click')
    expect(mocks.api.storeCredentials).toHaveBeenCalledWith({ clear: ['sshPrivateKey'] })
  })

  it('offers a clear control for every stored git credential', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    for (const field of ['sshPrivateKey', 'gitKnownHosts', 'gitUserName', 'gitUserEmail'])
      expect(wrapper.find(`[data-clear="${field}"]`).exists()).toBe(true)
  })

  it('toggling the clear control twice keeps the stored key', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    await wrapper.get('[data-clear="sshPrivateKey"]').trigger('click')
    await wrapper.get('[data-clear="sshPrivateKey"]').trigger('click')
    await wrapper.get('[data-save-credentials]').trigger('click')
    expect(mocks.api.storeCredentials).toHaveBeenCalledWith({})
  })

  it('groups the page into git, API key and provider login cards with labelled inputs', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    expect(wrapper.find('[data-card="git"]').exists()).toBe(true)
    expect(wrapper.find('[data-card="api-keys"]').exists()).toBe(true)
    expect(wrapper.find('[data-card="logins"]').exists()).toBe(true)
    // Every input is reachable through a label, so nothing is two unlabelled boxes glued together.
    for (const input of wrapper.findAll('input, textarea, select')) {
      const id = input.attributes('id')
      expect(id, `input ${input.attributes('data-credential') || input.attributes('data-git-pat-host') || ''} has an id`).toBeTruthy()
      expect(wrapper.find(`label[for="${id}"]`).exists(), `label for ${id}`).toBe(true)
    }
  })
})

/// Git tokens are a list keyed by host, not fixed per-provider slots: a company GitLab and the
/// public one can both be stored. Each add and remove is applied immediately, not on Save.
describe('Git personal access tokens', () => {
  const two = [
    { id: 'a', kind: 'gitlab', host: 'gitlab.example.com' },
    { id: 'b', kind: 'github', host: 'github.com' }
  ]

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.storeCredentials.mockResolvedValue(null)
  })

  it('renders one row per stored token with its kind and host, never a token', async () => {
    mocks.api.getCredentialStatus.mockResolvedValue({ gitPats: two })
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    const rows = wrapper.findAll('[data-git-pat]')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('GitLab')
    expect(rows[0].text()).toContain('gitlab.example.com')
    expect(rows[1].text()).toContain('GitHub')
    expect(rows[1].text()).toContain('github.com')
    expect(wrapper.find('[data-git-pat-empty]').exists()).toBe(false)
    // There is no password field with a value anywhere in the list.
    expect(wrapper.get('[data-git-pat-token]').element.value).toBe('')
  })

  it('shows an empty state and a disabled Add until host and token are filled in', async () => {
    mocks.api.getCredentialStatus.mockResolvedValue({})
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    expect(wrapper.find('[data-git-pat-empty]').exists()).toBe(true)
    expect(wrapper.get('[data-git-pat-add]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-git-pat-host]').setValue('gitlab.example.com')
    expect(wrapper.get('[data-git-pat-add]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-git-pat-token]').setValue('glpat-x')
    expect(wrapper.get('[data-git-pat-add]').attributes('disabled')).toBeUndefined()
  })

  it('posts a new token immediately and re-reads the list', async () => {
    mocks.api.getCredentialStatus
      .mockResolvedValueOnce({ gitPats: [] })
      .mockResolvedValueOnce({ gitPats: [two[1]] })
    mocks.api.addGitPat.mockResolvedValue({ id: 'b', kind: 'github', host: 'github.com' })
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()

    await wrapper.get('[data-git-pat-kind]').setValue('github')
    await wrapper.get('[data-git-pat-host]').setValue(' github.com ')
    await wrapper.get('[data-git-pat-token]').setValue('ghp_secret')
    await wrapper.get('[data-git-pat-add]').trigger('click')
    await flushPromises()

    expect(mocks.api.addGitPat).toHaveBeenCalledWith({ kind: 'github', host: 'github.com', token: 'ghp_secret' })
    expect(wrapper.findAll('[data-git-pat]')).toHaveLength(1)
    // The token field is emptied so it cannot be re-submitted or left on screen.
    expect(wrapper.get('[data-git-pat-token]').element.value).toBe('')
    expect(wrapper.get('[data-git-pat-host]').element.value).toBe('')
    // Not part of the staged Save payload.
    expect(mocks.api.storeCredentials).not.toHaveBeenCalled()
  })

  it('shows the backend validation message inline when adding fails', async () => {
    mocks.api.getCredentialStatus.mockResolvedValue({ gitPats: [] })
    mocks.api.addGitPat.mockRejectedValue(new Error('A token needs the host it belongs to'))
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    await wrapper.get('[data-git-pat-host]').setValue('bad host')
    await wrapper.get('[data-git-pat-token]').setValue('glpat-x')
    await wrapper.get('[data-git-pat-add]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-git-pat-error]').text()).toContain('needs the host')
    // The input keeps what the user typed so they can fix it.
    expect(wrapper.get('[data-git-pat-host]').element.value).toBe('bad host')
  })

  it('removes a token only after an inline confirmation, without a popup', async () => {
    mocks.api.getCredentialStatus
      .mockResolvedValueOnce({ gitPats: two })
      .mockResolvedValueOnce({ gitPats: [two[1]] })
    mocks.api.deleteGitPat.mockResolvedValue(null)
    const confirmSpy = vi.spyOn(window, 'confirm')
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()

    const remove = wrapper.get('[data-git-pat="gitlab.example.com"] [data-git-pat-remove]')
    await remove.trigger('click')
    expect(mocks.api.deleteGitPat).not.toHaveBeenCalled()
    expect(wrapper.get('[data-git-pat="gitlab.example.com"] [data-git-pat-remove]').text()).toContain('Really remove')
    expect(wrapper.find('[data-git-pat="gitlab.example.com"] [data-git-pat-keep]').exists()).toBe(true)

    await wrapper.get('[data-git-pat="gitlab.example.com"] [data-git-pat-remove]').trigger('click')
    await flushPromises()

    expect(confirmSpy).not.toHaveBeenCalled()
    expect(mocks.api.deleteGitPat).toHaveBeenCalledWith('a')
    expect(wrapper.findAll('[data-git-pat]')).toHaveLength(1)
    expect(wrapper.find('[data-git-pat="gitlab.example.com"]').exists()).toBe(false)
    confirmSpy.mockRestore()
  })

  it('Keep disarms the pending removal', async () => {
    mocks.api.getCredentialStatus.mockResolvedValue({ gitPats: two })
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    await wrapper.get('[data-git-pat="github.com"] [data-git-pat-remove]').trigger('click')
    await wrapper.get('[data-git-pat="github.com"] [data-git-pat-keep]').trigger('click')
    expect(wrapper.get('[data-git-pat="github.com"] [data-git-pat-remove]').text()).toBe('Remove')
    expect(wrapper.find('[data-git-pat-keep]').exists()).toBe(false)
    expect(mocks.api.deleteGitPat).not.toHaveBeenCalled()
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

describe('OpenCode credentials', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getCredentialStatus.mockResolvedValue({ openCodeApiKey: true, opencodeSubscription: true })
    mocks.api.storeCredentials.mockResolvedValue(null)
  })

  it('stores and clears the OpenCode API key without reading it back', async () => {
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()
    const input = wrapper.get('[data-credential="openCodeApiKey"]')
    expect(input.attributes('type')).toBe('password')
    expect(input.element.value).toBe('')
    expect(wrapper.get('[data-credential-hint="openCodeApiKey"]').text()).toContain('OpenCode Go')
    expect(wrapper.find('[data-credential-status="opencodeSubscription"]').exists()).toBe(true)
    expect(wrapper.find('[data-remove-subscription="OpenCode"]').exists()).toBe(true)
    await wrapper.get('[data-clear="openCodeApiKey"]').trigger('click')
    await wrapper.get('[data-save-credentials]').trigger('click')
    expect(mocks.api.storeCredentials).toHaveBeenCalledWith({ clear: ['openCodeApiKey'] })
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

  /// A subscription login is never typed in here — a runtime captured it from a session and
  /// uploaded it — so removing it is the one action the dialog can offer for it.
  it('removes a stored subscription login immediately and refreshes what is stored', async () => {
    mocks.api.deleteSubscriptionCredential.mockResolvedValue(null)
    mocks.api.getCredentialStatus
      .mockResolvedValueOnce({ openclawSubscription: true })
      .mockResolvedValueOnce({ openclawSubscription: false })
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()

    await wrapper.get('[data-remove-subscription="OpenClaw"]').trigger('click')
    await flushPromises()

    expect(mocks.api.deleteSubscriptionCredential).toHaveBeenCalledWith('OpenClaw')
    // Re-read rather than assumed: the delete is immediate, so the dialog must not keep
    // claiming a login is stored.
    expect(wrapper.find('[data-remove-subscription="OpenClaw"]').exists()).toBe(false)
    // Unlike the API key fields, this is not staged for save.
    expect(mocks.api.storeCredentials).not.toHaveBeenCalled()
  })

  it('keeps the login visible and reports why when the removal fails', async () => {
    mocks.api.deleteSubscriptionCredential.mockRejectedValue(new Error('backend said no'))
    const wrapper = mount(CredentialsDialog, { props: { embedded: true } })
    await flushPromises()

    await wrapper.get('[data-remove-subscription="OpenClaw"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-remove-subscription="OpenClaw"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('backend said no')
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
