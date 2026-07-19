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
    getCredentialStatus: vi.fn(), storeCredentials: vi.fn()
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

  it('changes untouched New-session policy defaults with the selected provider', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects: [] }, ...mountOptions })
    await wrapper.get('[data-agent-option="Codex"]').trigger('click')
    await wrapper.findAll('[data-mode-option]').find(button => button.text() === 'Autonomous').trigger('click')
    await wrapper.get('[data-advanced]').trigger('click')
    expect(wrapper.get('[data-policy="allowedTools"]').element.value).not.toContain('Bash(git*)')
    expect(wrapper.get('[data-policy="allowedCommands"]').element.value).toContain('git status')
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
