// @vitest-environment happy-dom
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn().mockResolvedValue(''),
    getConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 }),
    listPermissions: vi.fn().mockResolvedValue([]),
    decidePermission: vi.fn().mockResolvedValue({ decision: 'allow' }),
    updateSession: vi.fn().mockResolvedValue({})
  }
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  getSharedTranscript: vi.fn().mockResolvedValue(''),
  getSharedConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 })
}))

const session = (extra = {}) => ({ id: 's1', title: 'One', phase: 'Running', mode: 'Interactive', ...extra })

function mountView(props = {}) {
  return mount(TerminalView, {
    props: { session: session(), ...props },
    global: { stubs: { TerminalPane: true, ShareSessionDialog: true } }
  })
}

describe('in-app permission approvals', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listPermissions.mockResolvedValue([])
    vi.useFakeTimers()
  })
  afterEach(() => vi.useRealTimers())

  it('shows pending permission requests with allow/always/deny actions', async () => {
    mocks.api.listPermissions.mockResolvedValue([{ id: 'p1', tool: 'Bash', summary: 'Run a shell command.' }])
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('The agent wants to use Bash.')
    expect(wrapper.text()).toContain('Run a shell command.')
    const labels = wrapper.findAll('.perm-actions button').map(b => b.text())
    expect(labels).toEqual(['Allow', "Allow (don't ask again)", 'Allow everything', 'Deny'])
  })

  it('turns on auto approve from the prompt and clears the pending requests', async () => {
    mocks.api.listPermissions.mockResolvedValueOnce([{ id: 'p1', tool: 'Bash', summary: null }])
    const wrapper = mountView()
    await flushPromises()

    // The backend resolves the pending requests when auto approve is switched on.
    mocks.api.listPermissions.mockResolvedValue([])
    await wrapper.find('[data-auto-approve]').trigger('click')
    await flushPromises()

    expect(mocks.api.updateSession).toHaveBeenCalledWith('s1', { autoApprove: true })
    expect(wrapper.find('.perm.auto-on').exists()).toBe(true)
  })

  it('resolves a request and removes it from the list', async () => {
    mocks.api.listPermissions.mockResolvedValue([{ id: 'p1', tool: 'Bash', summary: null }])
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('.perm-actions button')[1].trigger('click')
    await flushPromises()

    expect(mocks.api.decidePermission).toHaveBeenCalledWith('s1', 'p1', 'allowAlways')
    expect(wrapper.find('.perm').exists()).toBe(false)
  })

  it('polls while the session is live', async () => {
    mountView()
    await flushPromises()
    expect(mocks.api.listPermissions).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(4000)
    expect(mocks.api.listPermissions).toHaveBeenCalledTimes(2)
  })

  it('does not poll for viewers, shared links or finished sessions', async () => {
    const viewer = mountView({ session: session({ accessRole: 'Viewer' }) })
    await flushPromises()
    expect(mocks.api.listPermissions).not.toHaveBeenCalled()
    viewer.unmount()

    const shared = mountView({ sharedToken: 'tok' })
    await flushPromises()
    expect(mocks.api.listPermissions).not.toHaveBeenCalled()
    shared.unmount()

    const done = mountView({ session: session({ phase: 'Succeeded' }) })
    await flushPromises()
    expect(mocks.api.listPermissions).not.toHaveBeenCalled()
    done.unmount()
  })
})
