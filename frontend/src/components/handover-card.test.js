// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import HandoverCard from './HandoverCard.vue'

const mocks = vi.hoisted(() => ({
  api: { adminHandover: vi.fn(), previewHandover: vi.fn(), applyHandover: vi.fn() }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const overview = (patch = {}) => ({
  available: true,
  sources: [{ owner: 'admin', sessions: 3, projects: 1 }],
  targets: [{ owner: 'alice', displayName: 'Alice', email: 'alice@example.com' }],
  history: [],
  ...patch
})

const preview = (patch = {}) => ({
  from: 'admin', to: 'alice', includeCredentials: false,
  moves: { sessions: 3, projects: 1, skills: 0 },
  stays: { apiTokens: 1, chatThreads: 0, usageLimits: 0 },
  renames: [{ kind: 'projects', name: 'Tools', newName: 'Tools (API key)', scope: '' }],
  credentials: [{ kind: 'gitPats', count: 2, kept: 0 }, { kind: 'providerLogins.claude', count: 1, kept: 0 }],
  storage: { configured: true, objects: 4, bytes: 2048 },
  scheduledSessions: 1, liveSessions: [], token: 'tok',
  ...patch
})

async function choose(wrapper, credentials = false) {
  await wrapper.find('[data-handover-to]').setValue('alice')
  if (credentials) await wrapper.find('[data-handover-credentials]').setValue(true)
}

describe('HandoverCard', () => {
  beforeEach(() => {
    vi.resetAllMocks()
    mocks.api.adminHandover.mockResolvedValue(overview())
  })

  it('stays hidden outside team login', async () => {
    mocks.api.adminHandover.mockResolvedValue(overview({ available: false }))
    const wrapper = mount(HandoverCard)
    await flushPromises()
    expect(wrapper.find('[data-handover]').exists()).toBe(false)
  })

  it('leaves credentials with the old owner unless ticked, and says so per kind', async () => {
    mocks.api.previewHandover.mockResolvedValue(preview())
    const wrapper = mount(HandoverCard)
    await flushPromises()
    await choose(wrapper)
    expect(wrapper.find('[data-handover-credentials-warning]').exists()).toBe(false)
    await wrapper.find('[data-handover-preview]').trigger('click')
    await flushPromises()

    expect(mocks.api.previewHandover).toHaveBeenCalledWith('admin', 'alice', false)
    const pats = wrapper.find('[data-handover-credential="gitPats"]').text()
    expect(pats).toContain('Git access tokens (2)')
    expect(pats).toContain('stays with admin; sessions that need them will ask for new credentials')
    expect(wrapper.find('[data-handover-credential="providerLogins.claude"]').text()).toContain('Claude logins (1)')
    expect(wrapper.find('[data-handover-rename]').text()).toContain('Tools (API key)')
    expect(wrapper.find('[data-handover-storage]').text()).toContain('4 objects, 2.0 KB')
    expect(wrapper.find('[data-handover-stays="apiTokens"]').exists()).toBe(true)
  })

  it('warns before credentials are handed over and applies the previewed token', async () => {
    mocks.api.previewHandover.mockResolvedValue(preview({ includeCredentials: true, credentials: [{ kind: 'sshKey', count: 1, kept: 1 }] }))
    mocks.api.applyHandover.mockResolvedValue({ audit: { to: 'alice' }, cleanupPending: false })
    const wrapper = mount(HandoverCard)
    await flushPromises()
    await choose(wrapper, true)
    expect(wrapper.find('[data-handover-credentials-warning]').text())
      .toContain("The target user will be able to use these logins and keys (for example someone else's model subscription or Git access).")
    await wrapper.find('[data-handover-preview]').trigger('click')
    await flushPromises()
    expect(mocks.api.previewHandover).toHaveBeenCalledWith('admin', 'alice', true)
    expect(wrapper.find('[data-handover-credential="sshKey"]').text()).toContain("0 move; 1 kept by alice's own")

    await wrapper.find('[data-handover-apply]').trigger('click')
    await flushPromises()
    expect(mocks.api.applyHandover).toHaveBeenCalledWith('tok')
    expect(wrapper.find('[data-handover-done]').text()).toContain('Handed over to alice.')
  })

  it('blocks while sessions run and forgets a preview when the choice changes', async () => {
    mocks.api.previewHandover.mockResolvedValue(preview({ liveSessions: ['s1'], token: null }))
    const wrapper = mount(HandoverCard)
    await flushPromises()
    await choose(wrapper)
    await wrapper.find('[data-handover-preview]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-handover-live]').text()).toContain('Pause these sessions first: s1')
    expect(wrapper.find('[data-handover-apply]').exists()).toBe(false)

    await wrapper.find('[data-handover-credentials]').setValue(true)
    expect(wrapper.find('[data-handover-result]').exists()).toBe(false)
  })
})
