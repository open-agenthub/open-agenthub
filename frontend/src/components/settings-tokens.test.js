// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SettingsDialog from './SettingsDialog.vue'

const mocks = vi.hoisted(() => ({
  config: { gitEnabled: false, slackEnabled: false, telegramEnabled: false, signalEnabled: false },
  api: {
    listApiTokens: vi.fn(),
    slackMe: vi.fn(), setSlackPrefs: vi.fn(),
    chatMe: vi.fn(), telegramLinkCode: vi.fn(), setTelegramPrefs: vi.fn(),
    unlinkTelegram: vi.fn(), setSignalPrefs: vi.fn(), verifySignal: vi.fn()
  }
}))

vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

function mountTokens() {
  return mount(SettingsDialog, { props: { embedded: true, section: 'tokens' } })
}

describe('API token help snippets', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listApiTokens.mockResolvedValue([])
  })

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
