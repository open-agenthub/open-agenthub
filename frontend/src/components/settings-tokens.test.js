// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SettingsDialog from './SettingsDialog.vue'

const mocks = vi.hoisted(() => ({
  config: { gitEnabled: false, slackEnabled: false, telegramEnabled: false, signalEnabled: false },
  api: {
    listApiTokens: vi.fn(), createApiToken: vi.fn(), updateApiToken: vi.fn(), deleteApiToken: vi.fn(),
    listProviderAccounts: vi.fn(), getCredentialStatus: vi.fn(),
    slackMe: vi.fn(), setSlackPrefs: vi.fn(),
    chatMe: vi.fn(), telegramLinkCode: vi.fn(), setTelegramPrefs: vi.fn(),
    unlinkTelegram: vi.fn(), setSignalPrefs: vi.fn(), verifySignal: vi.fn()
  }
}))

vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const work = { id: 'work0001', label: 'Work', email: 'me@example.com', isDefault: true }
const home = { id: 'home0002', label: 'Personal', email: 'me@home.example', isDefault: false }
const pats = [
  { id: 'pat-a', kind: 'gitlab', host: 'gitlab.example.com' },
  { id: 'pat-b', kind: 'github', host: 'github.com' }
]
const token = (overrides = {}) => ({
  id: 't1', name: 'laptop', prefix: 'oah_1a2b3c4d', createdAt: '2026-10-01T00:00:00Z', lastUsedAt: null,
  allowedCredentials: null, ...overrides
})

function mountTokens() {
  return mount(SettingsDialog, { props: { embedded: true, section: 'tokens' } })
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.listApiTokens.mockResolvedValue([])
  mocks.api.listProviderAccounts.mockResolvedValue({ Claude: [work, home], Codex: [], Cursor: [], OpenClaw: [] })
  mocks.api.getCredentialStatus.mockResolvedValue({ gitPats: pats })
  mocks.api.createApiToken.mockResolvedValue({ id: 't2', token: 'oah_fresh' })
  mocks.api.updateApiToken.mockResolvedValue(null)
  mocks.api.deleteApiToken.mockResolvedValue(null)
})

describe('API token help snippets', () => {
  it('shows curl and MCP snippets for remote clients', async () => {
    const wrapper = mountTokens()
    await flushPromises()

    const help = wrapper.get('details.help')
    await help.get('summary').trigger('click')

    const create = wrapper.get('[data-curl-create]').text()
    expect(create).toContain('/api/remote/sessions')
    expect(create).toContain('Authorization: Bearer <token>')

    const status = wrapper.get('[data-curl-status]').text()
    expect(status).toContain('/api/remote/sessions/<id>')

    const mcp = wrapper.get('[data-mcp-snippet]').text()
    expect(mcp).toContain('"command": "node"')
    expect(mcp).toContain('<path-to-repo>/mcp/agenthub/server.mjs')
    expect(mcp).toContain('AGENTHUB_URL')
    expect(mcp).toContain('AGENTHUB_TOKEN')
    expect(mcp).toContain(location.origin)
  })
})

/// A token can be restricted to some logins and git tokens (docs/credential-scopes.md). The
/// card is inline, the list shows a short form, and nothing here opens a browser popup.
describe('restricting an API token to credentials', () => {
  it('creates an unrestricted token unless the card is opened', async () => {
    const wrapper = mountTokens()
    await flushPromises()
    expect(wrapper.find('[data-token-scope-editor]').exists()).toBe(false)
    await wrapper.get('.create input').setValue('ci')
    await wrapper.get('.create button').trigger('click')
    await flushPromises()
    expect(mocks.api.createApiToken).toHaveBeenCalledWith('ci', null)
    expect(wrapper.get('.fresh-value').text()).toBe('oah_fresh')
  })

  it('opens the card with one checkbox per login and git token and sends the allow list', async () => {
    const wrapper = mountTokens()
    await flushPromises()
    await wrapper.get('[data-token-restrict]').setValue(true)
    const editor = wrapper.get('[data-token-create-scope]')
    expect(editor.text()).toContain('Work — me@example.com')
    expect(editor.find('[data-scope-empty]').exists()).toBe(true)
    expect(editor.findAll('[data-scope-pat]')).toHaveLength(2)

    await editor.get('[data-scope-account="Claude:work0001"]').setValue(true)
    await editor.get('[data-scope-pat="pat-b"]').setValue(true)
    await editor.get('[data-scope-api-keys]').setValue(true)
    expect(wrapper.find('[data-scope-empty]').exists()).toBe(false)

    await wrapper.get('.create input').setValue('ci')
    await wrapper.get('.create button').trigger('click')
    await flushPromises()
    expect(mocks.api.createApiToken).toHaveBeenCalledWith('ci', {
      providerAccounts: { Claude: ['work0001'] }, gitPats: ['pat-b'], apiKeys: true
    })
    // The form is back to an unrestricted next token.
    expect(wrapper.get('[data-token-restrict]').element.checked).toBe(false)
  })

  it('shows the short form of each token\'s restriction in the list', async () => {
    mocks.api.listApiTokens.mockResolvedValue([
      token(),
      token({ id: 't2', name: 'ci', allowedCredentials: { providerAccounts: { Claude: ['work0001'] }, gitPats: ['pat-a', 'pat-b'] } }),
      token({ id: 't3', name: 'any', allowedCredentials: { providerAccounts: { codex: ['*'] }, gitPats: ['*'], apiKeys: true } })
    ])
    const wrapper = mountTokens()
    await flushPromises()
    const summaries = wrapper.findAll('[data-token-scope]').map(s => s.text())
    expect(summaries).toEqual(['unrestricted', 'Claude: Work · git: 2 of 2', 'Codex: any · git: any · API keys'])
  })

  it('edits an existing restriction inline, prefilled, and can lift it', async () => {
    mocks.api.listApiTokens.mockResolvedValue([
      token({ allowedCredentials: { providerAccounts: { Claude: ['home0002'] }, gitPats: ['*'] } })
    ])
    const wrapper = mountTokens()
    await flushPromises()
    await wrapper.get('[data-token-edit-scope]').trigger('click')
    const edit = wrapper.get('[data-token-edit]')
    expect(edit.get('[data-scope-account="Claude:home0002"]').element.checked).toBe(true)
    expect(edit.get('[data-scope-account="Claude:work0001"]').element.checked).toBe(false)
    // "*" is shown as every token that exists now.
    expect(edit.findAll('[data-scope-pat]').every(box => box.element.checked)).toBe(true)

    await edit.get('[data-scope-pat="pat-a"]').setValue(false)
    await edit.get('[data-token-scope-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateApiToken).toHaveBeenCalledWith('t1', {
      providerAccounts: { Claude: ['home0002'] }, gitPats: ['pat-b'], apiKeys: false
    })
    expect(wrapper.find('[data-token-edit]').exists()).toBe(false)

    await wrapper.get('[data-token-edit-scope]').trigger('click')
    await wrapper.get('[data-token-scope-lift]').trigger('click')
    await flushPromises()
    expect(mocks.api.updateApiToken).toHaveBeenLastCalledWith('t1', null)
    expect(mocks.api.listApiTokens).toHaveBeenCalledTimes(3)
  })

  it('deletes only after an inline confirmation, never through a popup', async () => {
    mocks.api.listApiTokens.mockResolvedValueOnce([token()]).mockResolvedValue([])
    if (typeof window.confirm !== 'function') window.confirm = () => true
    const confirmSpy = vi.spyOn(window, 'confirm')
    const wrapper = mountTokens()
    await flushPromises()

    await wrapper.get('[data-token-delete]').trigger('click')
    expect(mocks.api.deleteApiToken).not.toHaveBeenCalled()
    expect(wrapper.get('[data-token-delete]').text()).toContain('Really delete')
    expect(wrapper.get('[data-token-delete-note]').text()).toContain('stops working')

    await wrapper.get('[data-token-keep]').trigger('click')
    expect(wrapper.get('[data-token-delete]').text()).toBe('Delete')
    expect(wrapper.find('[data-token-keep]').exists()).toBe(false)

    await wrapper.get('[data-token-delete]').trigger('click')
    await wrapper.get('[data-token-delete]').trigger('click')
    await flushPromises()
    expect(confirmSpy).not.toHaveBeenCalled()
    expect(mocks.api.deleteApiToken).toHaveBeenCalledWith('t1')
    expect(wrapper.findAll('[data-token]')).toHaveLength(0)
    confirmSpy.mockRestore()
  })

  it('still offers the API-key switch when the listings are unavailable', async () => {
    mocks.api.listProviderAccounts.mockRejectedValue(new Error('404'))
    mocks.api.getCredentialStatus.mockRejectedValue(new Error('404'))
    const wrapper = mountTokens()
    await flushPromises()
    await wrapper.get('[data-token-restrict]').setValue(true)
    expect(wrapper.find('[data-scope-no-accounts]').exists()).toBe(true)
    expect(wrapper.find('[data-scope-pats]').exists()).toBe(false)
    expect(wrapper.find('[data-scope-api-keys]').exists()).toBe(true)
  })
})
