// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import ShareSessionDialog from './ShareSessionDialog.vue'

// Only the dialog is imported here: the sharing-ui suite also mounts the shared-session page,
// and the MCP-policy regression below has to stay provable on its own.
const apiMocks = vi.hoisted(() => ({
  api: {
    listSessionShares: vi.fn(),
    createShareUser: vi.fn(), updateShareUser: vi.fn(), deleteShareUser: vi.fn(),
    createShareLink: vi.fn(), updateShareLink: vi.fn(), deleteShareLink: vi.fn(),
    updateMcpPolicy: vi.fn(),
    findShareRecipients: vi.fn().mockResolvedValue([]),
    getCredentialStatus: vi.fn(), switchSessionGitPats: vi.fn()
  }
}))

vi.mock('../api.js', () => ({ api: apiMocks.api }))

const session = { id: 's1', title: 'Owner session' }

// Every dialog is unmounted after its test, which clears its 250 ms recipient lookup (#518). The
// license tests type 'carol' and 'nobody' with real timers; left mounted, that lookup stayed
// pending, and on a loaded machine it landed inside a picker test after vi.clearAllMocks(), as a
// second findShareRecipients call. Timers go back to real here too, so a picker test that fails
// before its own cleanup cannot leave fake timers to the next one.
enableAutoUnmount(afterEach)
afterEach(() => { vi.useRealTimers() })

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

  it.each(['Cursor', 'OpenClaw'])('shows %s capabilities and preserves saved tool rules', async agent => {
    apiMocks.api.listSessionShares.mockResolvedValue({ users: [], links: [],
      mcpPolicy: { blockedServers: [], blockedTools: ['mcp__docs__write'] } })
    const wrapper = mount(ShareSessionDialog, { props: { session: { ...session, agent } } })
    await flushPromises()
    expect(wrapper.get('[data-blocked-tools]').element.disabled).toBe(true)
    expect(wrapper.text()).toContain(`Tool-level restrictions are not available for ${agent}`)
    expect(wrapper.text()).toContain('Restart the session')
    await wrapper.get('[data-blocked-servers]').setValue('docs')
    await wrapper.findAll('button').find(b => b.text() === 'Save MCP policy').trigger('click')
    await flushPromises()
    expect(apiMocks.api.updateMcpPolicy).toHaveBeenCalledWith('s1', {
      blockedServers: ['docs'], blockedTools: ['mcp__docs__write']
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

    await wrapper.get('[data-create-link]').trigger('click')
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
    expect(wrapper.find('[data-create-link]').exists()).toBe(false)
  })

  it('keeps the form and adds the gate when a change is refused with 402', async () => {
    apiMocks.api.listSessionShares.mockResolvedValue({
      users: [{ recipient: 'bob', role: 'Viewer' }], links: [], mcpPolicy: null
    })
    apiMocks.api.createShareUser.mockRejectedValue(license402())
    const wrapper = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()
    expect(wrapper.find('[data-license-gate]').exists()).toBe(false)

    await wrapper.get('input[aria-label="Recipient username or email"]').setValue('carol')
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

    await wrapper.get('input[aria-label="Recipient username or email"]').setValue('nobody')
    await wrapper.findAll('button').find(b => b.text() === 'Add').trigger('click')
    await flushPromises()

    expect(wrapper.get('.err').text()).toContain('unknown recipient')
    expect(wrapper.find('[data-license-gate]').exists()).toBe(false)
  })
})

/// Before a session is handed to someone, the owner sees which git identity it pushes with and can
/// move it to a group or project token (issue #132).
describe('ShareSessionDialog git identity', () => {
  const tokens = [
    { id: 'p', kind: 'gitlab', host: 'gitlab.example.com', label: 'personal', isDefault: true },
    { id: 'g', kind: 'gitlab', host: 'gitlab.example.com', label: 'group: platform', isDefault: false }
  ]

  beforeEach(() => {
    vi.clearAllMocks()
    apiMocks.api.listSessionShares.mockResolvedValue({ users: [], links: [], mcpPolicy: null })
    apiMocks.api.getCredentialStatus.mockResolvedValue({ gitPats: tokens })
  })

  it('says which token the session pushes with and switches it in place', async () => {
    apiMocks.api.switchSessionGitPats.mockResolvedValue({ session: { id: 's1', gitPatIds: ['g'] }, appliedNow: true })
    const wrapper = mount(ShareSessionDialog, { props: { session: { ...session, gitPatIds: null } } })
    await flushPromises()

    expect(wrapper.get('[data-share-git-in-use]').text()).toBe('Pushes as personal on gitlab.example.com')
    expect(wrapper.get('[data-share-git-apply]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-share-git-host="gitlab.example.com"]').setValue('g')
    await wrapper.get('[data-share-git-apply]').trigger('click')
    await flushPromises()

    expect(apiMocks.api.switchSessionGitPats).toHaveBeenCalledWith('s1', ['g'])
    expect(wrapper.get('[data-share-git-in-use]').text()).toBe('Pushes as group: platform on gitlab.example.com')
    expect(wrapper.get('[data-share-git-note]').text()).toContain('next push already uses it')
  })

  it('says "at the next start" when the pod could not take it, and hides itself without tokens', async () => {
    apiMocks.api.switchSessionGitPats.mockResolvedValue({ session: { id: 's1', gitPatIds: [] }, appliedNow: false })
    const wrapper = mount(ShareSessionDialog, { props: { session: { ...session, gitPatIds: ['p'] } } })
    await flushPromises()
    await wrapper.get('[data-share-git-host="gitlab.example.com"]').setValue('')
    await wrapper.get('[data-share-git-apply]').trigger('click')
    await flushPromises()
    expect(apiMocks.api.switchSessionGitPats).toHaveBeenCalledWith('s1', [])
    expect(wrapper.find('[data-share-git-none]').exists()).toBe(true)
    expect(wrapper.get('[data-share-git-note]').text()).toContain('next start')

    apiMocks.api.getCredentialStatus.mockResolvedValue({})
    const none = mount(ShareSessionDialog, { props: { session } })
    await flushPromises()
    expect(none.find('[data-share-git]').exists()).toBe(false)
  })
})

describe('recipient picker', () => {
  async function typed(text) {
    vi.clearAllMocks()
    vi.useFakeTimers()
    apiMocks.api.listSessionShares.mockResolvedValue({ users: [], links: [], mcpPolicy: null })
    apiMocks.api.findShareRecipients.mockResolvedValue([
      { username: 'bob', displayName: 'Bob Example' }, { username: 'bobby', displayName: 'Bobby Tables' }])
    apiMocks.api.createShareUser.mockResolvedValue({})
    const wrapper = mount(ShareSessionDialog, { props: { session }, attachTo: document.body })
    await flushPromises()
    expect(apiMocks.api.findShareRecipients).not.toHaveBeenCalled()
    const input = wrapper.get('[aria-label="Recipient username or email"]')
    await input.trigger('focus')
    await input.setValue(text)
    await vi.advanceTimersByTimeAsync(250)
    await flushPromises()
    return { wrapper, input }
  }
  const options = wrapper => wrapper.findAll('[aria-label="Matching recipients"] [role=option]')
  const listbox = wrapper => wrapper.get('[role=listbox]')

  // #470: the owner typed the full username, so writing it into the input changed nothing.
  it('selects a clicked suggestion visibly, closes the list and shares only on Add', async () => {
    const { wrapper, input } = await typed('bob')
    expect(apiMocks.api.findShareRecipients).toHaveBeenCalledTimes(1)
    expect(input.attributes('role')).toBe('combobox')
    expect(input.attributes('aria-expanded')).toBe('true')
    expect(options(wrapper).map(option => option.text())).toEqual(['Bob ExampleUsername: bob', 'Bobby TablesUsername: bobby'])

    await options(wrapper)[0].trigger('mousedown')
    await options(wrapper)[0].trigger('click')
    expect(input.element.value).toBe('Bob Example (bob)')
    expect(input.attributes('data-recipient-selected')).toBe('bob')
    expect(input.attributes('aria-expanded')).toBe('false')
    expect(listbox(wrapper).isVisible()).toBe(false)
    expect(wrapper.get('[data-recipient-chosen]').text()).toContain('Bob Example (Username: bob)')
    await vi.advanceTimersByTimeAsync(500)
    expect(apiMocks.api.findShareRecipients).toHaveBeenCalledTimes(1)
    expect(apiMocks.api.createShareUser).not.toHaveBeenCalled()

    await wrapper.get('[aria-label="Role for this person"]').setValue('Collaborator')
    await wrapper.findAll('button').find(button => button.text() === 'Add').trigger('click')
    await flushPromises()
    expect(apiMocks.api.createShareUser).toHaveBeenCalledWith('s1', { recipient: 'bob', role: 'Collaborator' })
    expect(input.element.value).toBe('')
    expect(wrapper.find('[data-recipient-chosen]').exists()).toBe(false)
  })

  it('moves with the arrow keys, selects with Enter and shares with a second Enter', async () => {
    const { wrapper, input } = await typed('Bob')
    await input.trigger('keydown', { key: 'ArrowDown' })
    expect(input.attributes('aria-activedescendant')).toBe(options(wrapper)[0].attributes('id'))
    expect(options(wrapper)[0].attributes('aria-selected')).toBe('true')
    await input.trigger('keydown', { key: 'ArrowDown' })
    await input.trigger('keydown', { key: 'ArrowDown' })
    expect(options(wrapper)[0].attributes('aria-selected')).toBe('true')
    await input.trigger('keydown', { key: 'ArrowUp' })
    expect(options(wrapper)[1].attributes('aria-selected')).toBe('true')
    await input.trigger('keydown', { key: 'Enter' })
    expect(input.element.value).toBe('Bobby Tables (bobby)')
    expect(listbox(wrapper).isVisible()).toBe(false)
    expect(apiMocks.api.createShareUser).not.toHaveBeenCalled()
    await input.trigger('keydown', { key: 'Enter' })
    await flushPromises()
    expect(apiMocks.api.createShareUser).toHaveBeenCalledWith('s1', { recipient: 'bobby', role: 'Viewer' })
  })

  it('closes the list on Escape, and typing again drops the selection', async () => {
    const { wrapper, input } = await typed('bo')
    expect(listbox(wrapper).isVisible()).toBe(true)
    await input.trigger('keydown', { key: 'Escape' })
    expect(listbox(wrapper).isVisible()).toBe(false)
    await input.trigger('keydown', { key: 'ArrowDown' })
    expect(listbox(wrapper).isVisible()).toBe(true)
    await input.trigger('keydown', { key: 'Enter' })
    expect(input.attributes('data-recipient-selected')).toBe('bob')
    await input.setValue('Bob Example (bo')
    expect(input.attributes('data-recipient-selected')).toBeUndefined()
    expect(input.element.value).toBe('Bob Example (bo')
  })

  it('still shares an exact username typed without a suggestion', async () => {
    const { wrapper, input } = await typed('carol@example.com')
    apiMocks.api.findShareRecipients.mockResolvedValue([])
    await input.trigger('keydown', { key: 'Enter' })
    await flushPromises()
    expect(apiMocks.api.createShareUser).toHaveBeenCalledWith('s1', { recipient: 'carol@example.com', role: 'Viewer' })
  })
})
