// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ShareSessionDialog from './ShareSessionDialog.vue'

// Only the dialog is imported here: the sharing-ui suite also mounts the shared-session page,
// and the MCP-policy regression below has to stay provable on its own.
const apiMocks = vi.hoisted(() => ({
  api: {
    listSessionShares: vi.fn(),
    createShareUser: vi.fn(), updateShareUser: vi.fn(), deleteShareUser: vi.fn(),
    createShareLink: vi.fn(), updateShareLink: vi.fn(), deleteShareLink: vi.fn(),
    updateMcpPolicy: vi.fn()
  }
}))

vi.mock('../api.js', () => ({ api: apiMocks.api }))

const session = { id: 's1', title: 'Owner session' }

describe('ShareSessionDialog MCP policy', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.api.updateMcpPolicy.mockResolvedValue({})
    apiMocks.api.createShareLink.mockResolvedValue({})
  })

  it('prefills the policy from the overview key the backend actually uses (mcpPolicy)', async () => {
    // The dialog used to read `policy`; the textareas came up empty on every open and saving one
    // restriction silently dropped the others.
    apiMocks.api.listSessionShares.mockResolvedValue({
      users: [],
      links: [],
      mcpPolicy: { blockedServers: ['filesystem', 'github'], blockedTools: ['mcp__github__create_issue'] }
    })
    const wrapper = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()

    expect(wrapper.get('[data-blocked-servers]').element.value).toBe('filesystem\ngithub')
    expect(wrapper.get('[data-blocked-tools]').element.value).toBe('mcp__github__create_issue')

    await wrapper.get('[data-blocked-servers]').setValue('filesystem\ngithub\nslack')
    await wrapper.findAll('button').find(b => b.text() === 'Save MCP policy').trigger('click')
    await flushPromises()

    expect(apiMocks.api.updateMcpPolicy).toHaveBeenCalledWith('s1', {
      blockedServers: ['filesystem', 'github', 'slack'],
      blockedTools: ['mcp__github__create_issue']
    })
  })

  it('shows empty policy fields when the session has no policy', async () => {
    apiMocks.api.listSessionShares.mockResolvedValue({ users: [], links: [], mcpPolicy: null })
    const wrapper = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()

    expect(wrapper.get('[data-blocked-servers]').element.value).toBe('')
    expect(wrapper.get('[data-blocked-tools]').element.value).toBe('')
  })

  it('shows the one-time url the backend returns for a new link, absolute or not', async () => {
    apiMocks.api.listSessionShares.mockResolvedValue({ users: [], links: [], mcpPolicy: null })
    apiMocks.api.createShareLink.mockResolvedValue({ link: { id: 'l1', role: 'Viewer' }, url: 'https://hub.example.com/shared/tok' })
    const wrapper = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Create link').trigger('click')
    await flushPromises()

    expect(wrapper.get('.one-time code').text()).toBe('https://hub.example.com/shared/tok')
  })
})
