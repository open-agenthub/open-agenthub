// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import GitSignInNote from './GitSignInNote.vue'
import AccountDialog from './AccountDialog.vue'

// #289: a connected provider that refused to renew its grant leaves the session without a token;
// the session and the account page say so and offer the sign-in.
const mocks = vi.hoisted(() => ({
  api: { gitProviders: vi.fn(), gitConnectUrl: vi.fn(), gitDisconnect: vi.fn() }
}))
vi.mock('../api.js', () => ({ api: mocks.api, config: {} }))

const refused = { id: 'gitlab', type: 'gitlab', displayName: 'GitLab', connected: true, needsSignIn: true, username: 'owner' }
const fine = { id: 'github', type: 'github', displayName: 'GitHub', connected: true, needsSignIn: false }
const session = { id: 's1', repos: [{ url: 'https://gitlab.example.com/group/project.git', providerId: 'gitlab' }] }

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.gitProviders.mockResolvedValue([refused, fine])
  mocks.api.gitConnectUrl.mockResolvedValue({ url: 'https://gitlab.example.com/oauth/authorize?state=x' })
})

describe('the session note', () => {
  it('names the provider the session uses and offers the sign-in', async () => {
    const wrapper = mount(GitSignInNote, { props: { session } })
    await flushPromises()
    const note = wrapper.get('[data-git-sign-in="gitlab"]')
    expect(note.text()).toContain('GitLab no longer accepts your stored sign-in')
    expect(note.text()).toContain('without a restart')
    expect(wrapper.find('[data-git-sign-in="github"]').exists()).toBe(false)
    await note.get('[data-git-sign-in-button]').trigger('click')
    await flushPromises()
    expect(mocks.api.gitConnectUrl).toHaveBeenCalledWith('gitlab')
  })

  it('stays silent for a session that does not use the refused provider, and asks nothing without providers', async () => {
    const other = mount(GitSignInNote, { props: { session: { id: 's2', repos: [{ url: 'https://github.com/a/b.git', providerId: 'github' }] } } })
    await flushPromises()
    expect(other.find('[data-git-sign-in]').exists()).toBe(false)

    mocks.api.gitProviders.mockClear()
    mount(GitSignInNote, { props: { session: { id: 's3', repos: [{ url: 'https://example.com/a.git' }] } } })
    await flushPromises()
    expect(mocks.api.gitProviders).not.toHaveBeenCalled()
  })
})

describe('Settings → Account', () => {
  it('shows a refused grant as needing a new sign-in', async () => {
    const wrapper = mount(AccountDialog, { props: { embedded: true } })
    await flushPromises()
    expect(wrapper.get('[data-git-needs-sign-in="gitlab"]').text()).toContain('sign in again')
    expect(wrapper.text()).toContain('Sign in again')
  })
})
