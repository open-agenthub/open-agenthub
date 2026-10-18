// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SecretsPane from './SecretsPane.vue'
import NewSessionDialog from './NewSessionDialog.vue'
import EditSessionDialog from './EditSessionDialog.vue'
import ProjectSecretsDialog from './ProjectSecretsDialog.vue'
import ShareSessionDialog from './ShareSessionDialog.vue'
import TokenScopeEditor from './TokenScopeEditor.vue'
import { emptyScopeDraft, scopeFromDraft, scopeSummary, draftFromScope } from '../lib/token-scope.js'

// The secret surfaces of docs/session-secrets.md: the settings tab, the session dialogs, the
// project selection and the token scope. Values are typed in and never come back.
const mocks = vi.hoisted(() => ({
  api: {
    listSecrets: vi.fn(), upsertSecret: vi.fn(), deleteSecret: vi.fn(), updateProject: vi.fn(),
    createSession: vi.fn(), updateSession: vi.fn(), getCredentialStatus: vi.fn(), getAllowedAgents: vi.fn(),
    listSessionShares: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const stored = [
  { id: 's1', name: 'DEPLOY_TOKEN', delivery: 'env', description: 'CI deploy', updatedAt: '2026-10-17T00:00:00Z' },
  { id: 's2', name: 'sa.json', delivery: 'file', description: null, updatedAt: '2026-10-17T00:00:00Z' }
]
const mountOptions = { global: { stubs: { RepoPicker: { template: '<div></div>' } } } }

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.listSecrets.mockResolvedValue(stored)
  mocks.api.upsertSecret.mockResolvedValue({ id: 's3', name: 'NEW_TOKEN', delivery: 'env' })
  mocks.api.deleteSecret.mockResolvedValue(null)
  mocks.api.updateProject.mockResolvedValue({ id: 'p1', name: 'Ops', secretIds: ['s1'] })
  mocks.api.createSession.mockResolvedValue({ id: 'new' })
  mocks.api.updateSession.mockResolvedValue({ id: 'x' })
  mocks.api.getCredentialStatus.mockResolvedValue({})
  mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude'] })
})

describe('Settings → Secrets', () => {
  it('lists names and delivery, and never has a value to show', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    expect(wrapper.findAll('[data-secret]').map(row => row.attributes('data-secret'))).toEqual(['DEPLOY_TOKEN', 'sa.json'])
    expect(wrapper.get('[data-secret="DEPLOY_TOKEN"]').text()).toContain('CI deploy')
  })

  it('adds a secret and drops the typed value from the page once stored', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-secret-add]').trigger('click')
    await wrapper.get('[data-secret-name]').setValue('NEW_TOKEN')
    expect(wrapper.get('[data-secret-value]').attributes('type')).toBe('password')
    await wrapper.get('[data-secret-value]').setValue('tok-123456789')
    await wrapper.get('[data-secret-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.upsertSecret).toHaveBeenCalledWith({ name: 'NEW_TOKEN', delivery: 'env', value: 'tok-123456789', description: null })
    expect(wrapper.find('[data-secret-form]').exists()).toBe(false)
    expect(wrapper.html()).not.toContain('tok-123456789')
  })

  it('edits without a value keep the stored one, and the name cannot be changed', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-secret="sa.json"] [data-secret-edit]').trigger('click')
    expect(wrapper.get('[data-secret-name]').attributes('readonly')).toBeDefined()
    expect(wrapper.get('[data-secret-value]').element.tagName).toBe('TEXTAREA')
    expect(wrapper.get('[data-secret-value]').element.value).toBe('')
    await wrapper.get('[data-secret-description]').setValue('GCP key')
    await wrapper.get('[data-secret-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.upsertSecret).toHaveBeenCalledWith({ name: 'sa.json', delivery: 'file', value: null, description: 'GCP key' })
  })

  it('shows the 400 the backend gives for a reserved name', async () => {
    mocks.api.upsertSecret.mockRejectedValue(new Error("400 'HOME' is set by the session runtime and cannot be overridden by a secret."))
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-secret-add]').trigger('click')
    await wrapper.get('[data-secret-name]').setValue('HOME')
    await wrapper.get('[data-secret-value]').setValue('x')
    await wrapper.get('[data-secret-save]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-secret-error]').text()).toContain('cannot be overridden')
  })

  it('removes in two steps, without a browser popup', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-secret="DEPLOY_TOKEN"] [data-secret-remove]').trigger('click')
    expect(mocks.api.deleteSecret).not.toHaveBeenCalled()
    await wrapper.get('[data-secret="DEPLOY_TOKEN"] [data-secret-remove]').trigger('click')
    await flushPromises()
    expect(mocks.api.deleteSecret).toHaveBeenCalledWith('s1')
  })

  it('opens a prefilled form for an agent request, the existing entry when the name is stored', async () => {
    const fresh = mount(SecretsPane, { props: { prefill: { name: 'SENTRY_DSN', delivery: 'env', description: 'error reporting' } } })
    await flushPromises()
    expect(fresh.get('[data-secret-name]').element.value).toBe('SENTRY_DSN')
    expect(fresh.get('[data-secret-description]').element.value).toBe('error reporting')
    expect(fresh.get('[data-secret-value]').element.value).toBe('')

    const known = mount(SecretsPane, { props: { prefill: { name: 'deploy_token', delivery: 'env', description: 'for the release' } } })
    await flushPromises()
    expect(known.get('[data-secret-name]').element.value).toBe('DEPLOY_TOKEN')
    expect(known.get('[data-secret-name]').attributes('readonly')).toBeDefined()
  })
})

describe('secret selection in the session dialogs', () => {
  const projects = [{ id: 'p1', name: 'Ops', secretIds: ['s1'] }, { id: 'p2', name: 'Empty', secretIds: [] }]

  it('outside a project nothing is ticked and nothing is sent', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects }, ...mountOptions })
    await flushPromises()
    expect(wrapper.findAll('[data-secret-option]')).toHaveLength(2)
    expect(wrapper.findAll('[data-secret-option]').some(box => box.element.checked)).toBe(false)
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSession.mock.calls[0][0].secretIds).toBeNull()
  })

  it('in a project the project selection is ticked and followed; unticking narrows', async () => {
    const wrapper = mount(NewSessionDialog, { props: { projects }, ...mountOptions })
    await flushPromises()
    await wrapper.find('select[class]').exists()
    const projectSelect = wrapper.findAll('select').find(select => select.html().includes('No project'))
    await projectSelect.setValue('p1')
    await flushPromises()
    expect(wrapper.findAll('[data-secret-option]').map(box => box.attributes('data-secret-option'))).toEqual(['s1'])
    expect(wrapper.get('[data-secret-option="s1"]').element.checked).toBe(true)
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSession.mock.calls[0][0].secretIds).toBeNull()

    await wrapper.get('[data-secret-option="s1"]').setValue(false)
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.createSession.mock.calls[1][0].secretIds).toEqual([])

    await projectSelect.setValue('p2')
    await flushPromises()
    expect(wrapper.find('[data-secret-none]').exists()).toBe(true)
  })

  it('an edit sends the wildcard to follow the project again, and nothing when untouched', async () => {
    const session = { id: 'x', title: 'X', mode: 'Interactive', agent: 'Claude', authMode: 'Subscription',
      policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }, repos: [], projectId: 'p1', secretIds: [] }
    const wrapper = mount(EditSessionDialog, { props: { session, projects }, ...mountOptions })
    await flushPromises()
    expect(wrapper.get('[data-secret-option="s1"]').element.checked).toBe(false)
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSession.mock.calls[0][1]).not.toHaveProperty('secretIds')

    await wrapper.get('[data-secret-option="s1"]').setValue(true)
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateSession.mock.calls[1][1].secretIds).toEqual(['*'])
  })
})

describe('a project selects secrets', () => {
  it('saves the ticked ids, leaving out one removed in the meantime', async () => {
    const wrapper = mount(ProjectSecretsDialog, { props: { project: { id: 'p1', name: 'Ops', secretIds: ['s1', 'gone'] } } })
    await flushPromises()
    expect(wrapper.get('[data-project-secret="s1"]').element.checked).toBe(true)
    await wrapper.get('[data-project-secret="s2"]').setValue(true)
    await wrapper.get('[data-project-secrets-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateProject).toHaveBeenCalledWith('p1', { secretIds: ['s1', 's2'] })
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })
})

describe('a token restricted to secrets', () => {
  it('ticks secrets into the scope and omits the part when none is ticked', async () => {
    const wrapper = mount(TokenScopeEditor, { props: { modelValue: emptyScopeDraft(), secrets: stored } })
    await wrapper.get('[data-scope-secret="s2"]').trigger('change')
    const draft = wrapper.emitted('update:modelValue')[0][0]
    expect(scopeFromDraft(draft).secrets).toEqual(['s2'])
    expect(scopeFromDraft(emptyScopeDraft())).not.toHaveProperty('secrets')
    expect(draftFromScope({ secrets: ['*'] }, {}, [], stored).secrets).toEqual(['s1', 's2'])
    expect(scopeSummary({ secrets: ['s1'] }, {}, [], stored)).toBe('secrets: 1 of 2')
  })
})

describe('sharing a session', () => {
  it('says that terminal input reaches the secrets the session holds', async () => {
    mocks.api.listSessionShares.mockResolvedValue({ shares: [] })
    const wrapper = mount(ShareSessionDialog, { props: { session: { id: 's', title: 'S' } } })
    await flushPromises()
    expect(wrapper.get('[data-share-secrets-note]').text()).toContain('~/.secrets')
  })
})
