// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SecretsPane from './SecretsPane.vue'
import { provenanceLabel, secretUsage } from '../lib/secrets.js'

// #254: each stored secret says which projects select it and which sessions have it, and how.
const mocks = vi.hoisted(() => ({
  api: {
    listSecrets: vi.fn(), upsertSecret: vi.fn(), deleteSecret: vi.fn(), listSessions: vi.fn(), listProjects: vi.fn(),
    listSecretRequests: vi.fn(), updateProject: vi.fn()
  }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: {} }))

const stored = [
  { id: 'k1', name: 'API_KEY', delivery: 'env', description: 'production' },
  { id: 'k2', name: 'kube.yaml', delivery: 'file' },
  { id: 'k3', name: 'UNUSED', delivery: 'env' }
]
const projects = [{ id: 'p1', name: 'Deploys', secretIds: ['k1'] }, { id: 'p2', name: 'Other', secretIds: [] }]
const sessions = [
  // The hub marks an ordinary selection inheritable too (a child gets its parent's selection); the
  // note is shown for grants, where it is the owner's choice.
  { id: 's1', title: 'deploy-bot', projectId: 'p1', effectiveSecrets: [{ id: 'k1', provenance: 'project', inheritToChildren: true }] },
  { id: 's2', title: 'child', projectId: 'p2', effectiveSecrets: [{ id: 'k1', provenance: 'inherited' }, { id: 'k2', provenance: 'requested' }] },
  { id: 's3', title: 'loose', effectiveSecrets: [{ id: 'k2', provenance: 'granted', inheritToChildren: true }] },
  { id: 's4', title: 'outside', effectiveSecrets: [{ id: 'k2', provenance: 'project' }] },
  // Someone else's session shared with the owner: its secrets are its owner's, never listed here.
  { id: 'x9', title: 'shared', sharedBy: 'someone', effectiveSecrets: [{ id: 'k1', provenance: 'project' }] }
]

beforeEach(() => {
  vi.clearAllMocks()
  window.history.replaceState(null, '', '/settings/secrets')
  mocks.api.listSecrets.mockResolvedValue(stored)
  mocks.api.listSessions.mockResolvedValue(sessions)
  mocks.api.listProjects.mockResolvedValue(projects)
  mocks.api.listSecretRequests.mockResolvedValue([])
  mocks.api.deleteSecret.mockResolvedValue(null)
  mocks.api.updateProject.mockResolvedValue({ ...projects[0], secretIds: [] })
})

describe('where a secret is used', () => {
  it('collects projects and the owner\'s own sessions with their provenance', () => {
    const k1 = secretUsage('k1', sessions.filter(s => !s.sharedBy), projects)
    expect(k1.projects.map(p => p.id)).toEqual(['p1'])
    expect(k1.sessions.map(u => [u.session.id, provenanceLabel(u, projects)])).toEqual([
      ['s1', 'via project Deploys'], ['s2', 'inherited from its parent']])
    expect(secretUsage('k2', sessions, projects).sessions.map(u => provenanceLabel(u, projects)))
      .toEqual(['requested by the agent', 'granted directly', 'selected for it'])
  })

  it('shows it under each secret, with links, and says when nothing uses one', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    const k1 = wrapper.get('[data-secret-usage="k1"]')
    expect(k1.findAll('[data-usage-project]').map(b => b.text())).toEqual(['Deploys'])
    expect(k1.findAll('[data-usage-session]').map(a => [a.text(), a.attributes('href')]))
      .toEqual([['deploy-bot', '/s/s1'], ['child', '/s/s2']])
    expect(k1.text()).not.toContain('shared')
    expect(k1.text()).not.toContain('passed to child sessions')
    expect(wrapper.get('[data-secret-usage="k2"]').text()).toContain('loose (granted directly, passed to child sessions)')
    expect(wrapper.get('[data-secret-usage="k3"] [data-secret-unused]').text()).toBe('Not used by any project or session.')
  })

  it('a session link navigates inside the app', async () => {
    const seen = []
    const listener = () => seen.push(window.location.pathname)
    window.addEventListener('popstate', listener)
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-usage-session="s3"]').trigger('click')
    window.removeEventListener('popstate', listener)
    expect(seen).toEqual(['/s/s3'])
  })

  it('a project link opens its secret selection, and saving it refreshes the reach', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-usage-project="p1"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-project-secrets]').exists()).toBe(true)
    mocks.api.listProjects.mockResolvedValue([{ ...projects[0], secretIds: [] }, projects[1]])
    await wrapper.get('[data-project-secret="k1"]').setValue(false)
    await wrapper.get('[data-project-secrets-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateProject).toHaveBeenCalledWith('p1', { secretIds: [] })
    expect(wrapper.find('[data-project-secrets]').exists()).toBe(false)
    expect(wrapper.get('[data-secret-usage="k1"]').findAll('[data-usage-project]')).toHaveLength(0)
  })

  it('removing says how many sessions lose it before the second click', async () => {
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-secret="API_KEY"] [data-secret-remove]').trigger('click')
    expect(wrapper.get('[data-secret="API_KEY"] [data-secret-remove]').text()).toBe('Really remove? 2 sessions lose it')
    expect(mocks.api.deleteSecret).not.toHaveBeenCalled()
  })
})
