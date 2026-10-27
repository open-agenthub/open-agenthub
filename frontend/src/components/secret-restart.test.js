// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SecretRestartNote from './SecretRestartNote.vue'
import SecretsPane from './SecretsPane.vue'
import EditSessionDialog from './EditSessionDialog.vue'

// #255: a running container keeps its variables; the hub reports secretsPendingRestart and these
// surfaces say so with the restart that applies them.
const mocks = vi.hoisted(() => ({
  api: {
    listSecrets: vi.fn(), upsertSecret: vi.fn(), deleteSecret: vi.fn(), listSessions: vi.fn(),
    applySessionResources: vi.fn(), resyncSessionSecrets: vi.fn(), updateSession: vi.fn(), listSecretRequests: vi.fn(),
    getCredentialStatus: vi.fn(), getAllowedAgents: vi.fn()
  },
  config: { gitEnabled: false }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const running = { id: 'r1', title: 'deploy-bot', mode: 'Interactive', phase: 'Running', secretsPendingRestart: true }
const stored = [{ id: 'e1', name: 'DEPLOY_TOKEN', delivery: 'env' }, { id: 'f1', name: 'kube.yaml', delivery: 'file' }]

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.listSecrets.mockResolvedValue(stored)
  mocks.api.listSecretRequests.mockResolvedValue([])
  mocks.api.upsertSecret.mockResolvedValue({ id: 'e1', name: 'DEPLOY_TOKEN', delivery: 'env' })
  mocks.api.listSessions.mockResolvedValue([running, { id: 'r2', title: 'idle', secretsPendingRestart: false }])
  mocks.api.applySessionResources.mockResolvedValue({ ...running, secretsPendingRestart: false })
  mocks.api.updateSession.mockResolvedValue({ id: 'x' })
  mocks.api.getCredentialStatus.mockResolvedValue({})
  mocks.api.getAllowedAgents.mockResolvedValue({ agents: ['Claude'] })
})

describe('the restart note', () => {
  it('says variables wait for a restart and restarts in two clicks', async () => {
    const wrapper = mount(SecretRestartNote, { props: { session: running } })
    expect(wrapper.text()).toContain('reach the agent at its next start')
    await wrapper.get('[data-secret-restart]').trigger('click')
    expect(mocks.api.applySessionResources).not.toHaveBeenCalled()
    await wrapper.get('[data-secret-restart]').trigger('click')
    await flushPromises()
    expect(mocks.api.applySessionResources).toHaveBeenCalledWith('r1')
    expect(wrapper.emitted('restarted')).toHaveLength(1)
  })

  it('offers no restart for a session that cannot be restarted now, and is absent without a pending change', () => {
    const autonomous = mount(SecretRestartNote, { props: { session: { ...running, mode: 'Autonomous' } } })
    expect(autonomous.find('[data-secret-restart]').exists()).toBe(false)
    expect(autonomous.text()).toContain('not restarted for them')
    expect(mount(SecretRestartNote, { props: { session: { ...running, secretsPendingRestart: false } } }).html()).toBe('<!--v-if-->')
  })
})

// #301: a schedule whose template could not follow a change says so and updates in one click,
// because updating it restarts nothing.
describe('the schedule note', () => {
  const schedule = { id: 's1', title: 'nightly', mode: 'Scheduled', phase: 'Scheduled', scheduleSecretsOutdated: true }

  it('explains the next run would use the previous variables and updates the schedule at once', async () => {
    mocks.api.applySessionResources.mockResolvedValue({ ...schedule, scheduleSecretsOutdated: false })
    const wrapper = mount(SecretRestartNote, { props: { session: schedule } })
    expect(wrapper.text()).toContain('next scheduled run would still start with the previous secret variables')
    expect(wrapper.find('[data-secret-restart]').exists()).toBe(false)
    await wrapper.get('[data-schedule-secrets-update]').trigger('click')
    await flushPromises()
    expect(mocks.api.applySessionResources).toHaveBeenCalledWith('s1')
    expect(wrapper.emitted('restarted')[0][0].scheduleSecretsOutdated).toBe(false)
  })

  it('shows why an update was refused', async () => {
    mocks.api.applySessionResources.mockRejectedValue(new Error('409 Finish its project move'))
    const wrapper = mount(SecretRestartNote, { props: { session: schedule } })
    await wrapper.get('[data-schedule-secrets-update]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('Finish its project move')
  })
})

// #303: copies withdrawn after a failed rewrite are visible on the session, with a retry, and the
// restart is still offered because the running process keeps what it started with.
describe('the withdrawn note', () => {
  const withdrawn = { ...running, secretsWithdrawn: true, secretsPendingRestart: true }

  it('says the secrets are withdrawn, retries without restarting, and still offers the restart', async () => {
    mocks.api.resyncSessionSecrets.mockResolvedValue({ ...running, secretsWithdrawn: false })
    const wrapper = mount(SecretRestartNote, { props: { session: withdrawn } })
    expect(wrapper.get('[data-secrets-withdrawn-note]').text()).toContain('secrets are withdrawn')
    expect(wrapper.get('[data-secret-restart-note]').text()).toContain('including any secret deleted since it started')
    expect(wrapper.get('[data-secret-restart-note]').text()).not.toContain('already current')
    await wrapper.get('[data-secrets-resync]').trigger('click')
    await flushPromises()
    expect(mocks.api.resyncSessionSecrets).toHaveBeenCalledWith('r1')
    expect(mocks.api.applySessionResources).not.toHaveBeenCalled()
    expect(wrapper.emitted('restarted')[0][0].secretsWithdrawn).toBe(false)
  })

  it('shows a retry that is still refused', async () => {
    mocks.api.resyncSessionSecrets.mockRejectedValue(new Error('1 session(s) may still hold the previous secret values'))
    const wrapper = mount(SecretRestartNote, { props: { session: withdrawn } })
    await wrapper.get('[data-secrets-resync]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-secrets-withdrawn-note] [role="alert"]').text()).toContain('may still hold')
  })
})

describe('Settings → Secrets after a change', () => {
  it('lists the running sessions whose variables are now stale', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    expect(wrapper.find('[data-secret-restart-note]').exists()).toBe(false)
    await wrapper.get('[data-secret="DEPLOY_TOKEN"] [data-secret-edit]').trigger('click')
    await wrapper.get('[data-secret-value]').setValue('new-value-1234')
    await wrapper.get('[data-secret-save]').trigger('click')
    await flushPromises()
    const notes = wrapper.findAll('[data-secret-restart-note]')
    expect(notes.map(n => n.attributes('data-secret-restart-note'))).toEqual(['r1'])
    expect(notes[0].text()).toContain('“deploy-bot”')
  })
})

describe('the edit dialog', () => {
  const session = {
    id: 'x', title: 'X', mode: 'Interactive', phase: 'Running', agent: 'Claude', authMode: 'Subscription',
    policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }, repos: [], secretIds: ['e1'],
    effectiveSecrets: [{ id: 'e1', name: 'DEPLOY_TOKEN', delivery: 'env', provenance: 'project' }]
  }
  const mountOptions = { props: { session, projects: [] }, global: { stubs: { RepoPicker: { template: '<div></div>' } } } }

  it('offers the restart when a variable is added or removed, not for a file', async () => {
    const wrapper = mount(EditSessionDialog, mountOptions)
    await flushPromises()
    expect(wrapper.find('[data-resources-restart]').exists()).toBe(false)
    await wrapper.get('[data-secret-grant="f1"] [data-grant-level="session"]').trigger('click')
    expect(wrapper.find('[data-resources-restart]').exists()).toBe(false)
    await wrapper.get('[data-secret-grant="e1"] [data-grant-level="none"]').trigger('click')
    expect(wrapper.get('[data-resources-restart]').text()).toContain('apply the new secret variables')
    await wrapper.get('[data-submit]').trigger('click')
    await flushPromises()
    expect(mocks.api.applySessionResources).toHaveBeenCalledWith('x')
  })

  it('offers it for a change made elsewhere that the running process does not have yet', async () => {
    const wrapper = mount(EditSessionDialog, { ...mountOptions, props: { ...mountOptions.props, session: { ...session, secretsPendingRestart: true } } })
    await flushPromises()
    expect(wrapper.get('[data-resources-restart]').text()).toContain('apply the new secret variables')
  })
})
