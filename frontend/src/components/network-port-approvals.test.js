// @vitest-environment happy-dom
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn().mockResolvedValue(''),
    listPermissions: vi.fn().mockResolvedValue([]),
    decidePermission: vi.fn().mockResolvedValue({ decision: 'allow' }),
    updateSession: vi.fn().mockResolvedValue({})
  }
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  getSharedTranscript: vi.fn().mockResolvedValue('')
}))

const session = (extra = {}) => ({ id: 's1', title: 'One', phase: 'Running', mode: 'Interactive', ...extra })

function mountView(props = {}) {
  return mount(TerminalView, {
    props: { session: session(), ...props },
    global: { stubs: { TerminalPane: true, ShareSessionDialog: true } }
  })
}

describe('in-app network port approvals', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listPermissions.mockResolvedValue([])
    vi.useFakeTimers()
  })
  afterEach(() => vi.useRealTimers())

  it('renders an egress port request as a human sentence with the reason', async () => {
    mocks.api.listPermissions.mockResolvedValue([{
      id: 'n1', tool: 'NetworkPort(egress 5432/TCP)', summary: 'Connect to the staging Postgres.'
    }])
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('The agent asks to open network port 5432 (outgoing traffic, TCP).')
    expect(wrapper.text()).toContain('Connect to the staging Postgres.')
    // Same inline banner as tool permissions — no popup.
    expect(wrapper.find('.perm').exists()).toBe(true)
    const labels = wrapper.findAll('.perm-actions button').map(b => b.text())
    expect(labels).toEqual(['Allow', "Allow (don't ask again)", 'Allow everything', 'Deny'])
  })

  it('renders a browser-to-agent request with the direction label', async () => {
    mocks.api.listPermissions.mockResolvedValue([{
      id: 'n2', tool: 'NetworkPort(browser_to_agent 3000/TCP)', summary: null
    }])
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('The agent asks to open network port 3000 (browser → agent, TCP).')
  })

  it('allows a port request and removes it from the list', async () => {
    mocks.api.listPermissions.mockResolvedValue([{
      id: 'n1', tool: 'NetworkPort(egress 5432/TCP)', summary: null
    }])
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('.perm-actions button')[0].trigger('click')
    await flushPromises()

    expect(mocks.api.decidePermission).toHaveBeenCalledWith('s1', 'n1', 'allow')
    expect(wrapper.find('.perm').exists()).toBe(false)
  })

  it('denies a port request', async () => {
    mocks.api.listPermissions.mockResolvedValue([{
      id: 'n1', tool: 'NetworkPort(egress 5432/TCP)', summary: null
    }])
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('.perm-actions button')[3].trigger('click')
    await flushPromises()

    expect(mocks.api.decidePermission).toHaveBeenCalledWith('s1', 'n1', 'deny')
  })
})
