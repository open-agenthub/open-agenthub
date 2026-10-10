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

describe('ShareSessionDialog without a license', () => {
  const license402 = () => Object.assign(
    new Error('402 {"error":"An active enterprise license is required."}'),
    { status: 402, code: 'license_required' })

  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.api.createShareUser.mockResolvedValue({})
  })

  it('replaces the form with the license gate when the overview answers 402', async () => {
    // This is the raw `402 {"error":…}` text an owner saw on a Community instance.
    apiMocks.api.listSessionShares.mockRejectedValue(license402())
    const wrapper = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()

    expect(wrapper.get('[data-license-gate]').text()).toContain('Sharing sessions with other users')
    expect(wrapper.text()).not.toContain('An active enterprise license is required')
    expect(wrapper.text()).not.toContain('402')
    expect(wrapper.find('[data-blocked-servers]').exists()).toBe(false)
    expect(wrapper.findAll('button').find(b => b.text() === 'Create link')).toBeUndefined()
  })

  it('keeps the form and adds the gate when a change is refused with 402', async () => {
    apiMocks.api.listSessionShares.mockResolvedValue({
      users: [{ recipient: 'bob', role: 'Viewer' }], links: [], mcpPolicy: null
    })
    apiMocks.api.createShareUser.mockRejectedValue(license402())
    const wrapper = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()
    expect(wrapper.find('[data-license-gate]').exists()).toBe(false)

    await wrapper.get('input[placeholder="Add people by username…"]').setValue('carol')
    await wrapper.findAll('button').find(b => b.text() === 'Add').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-license-gate]').exists()).toBe(true)
    expect(wrapper.text()).toContain('bob') // the existing shares stay visible
    expect(wrapper.find('[data-blocked-servers]').exists()).toBe(true)
    expect(wrapper.find('.err').exists()).toBe(false)
  })

  it('still shows other failures as the inline error line', async () => {
    apiMocks.api.listSessionShares.mockResolvedValue({ users: [], links: [], mcpPolicy: null })
    apiMocks.api.createShareUser.mockRejectedValue(Object.assign(new Error('400 {"error":"unknown recipient"}'), { status: 400 }))
    const wrapper = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()

    await wrapper.get('input[placeholder="Add people by username…"]').setValue('nobody')
    await wrapper.findAll('button').find(b => b.text() === 'Add').trigger('click')
    await flushPromises()

    expect(wrapper.get('.err').text()).toContain('unknown recipient')
    expect(wrapper.find('[data-license-gate]').exists()).toBe(false)
  })
})
