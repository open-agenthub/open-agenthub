// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, onMounted } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from '../TerminalView.vue'
import { parseActivityFrame } from '../../lib/agent-activity.js'

// #562: a turn without progress says so, and offers the CLI's own interruption.
const mocks = vi.hoisted(() => ({
  api: {
    getConversation: vi.fn(), listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([])
  },
  interruptQueue: vi.fn(), activity: null
}))
vi.mock('../../api.js', () => ({ api: mocks.api, getSharedConversation: vi.fn() }))
vi.mock('../../lib/mermaid.js', () => ({ renderMermaidBlocks: vi.fn() }))

const wrappers = []
function view(session) {
  const wrapper = mount(TerminalView, {
    props: { session, initialUi: 'workspace' },
    global: { stubs: {
      SessionWorkspace: { template: '<div><slot /></div>' }, ShareSessionDialog: true,
      TerminalPane: defineComponent({
        props: ['kind'], emits: ['status', 'menu', 'activity'],
        setup(props, { emit, expose }) {
          expose({ submitMessage: vi.fn(), interruptAgent: vi.fn(), interruptQueue: mocks.interruptQueue, sendKeys: vi.fn() })
          onMounted(() => {
            emit('status', 'connected')
            if (props.kind === 'agent') mocks.activity = value => emit('activity', value)
          })
          return () => null
        }
      })
    } }
  })
  wrappers.push(wrapper)
  return wrapper
}
const interactive = { id: 's1', agent: 'Codex', title: 'Work', phase: 'Running', mode: 'Interactive', accessRole: 'Owner' }

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.getConversation.mockResolvedValue({ source: 'native', entries: [], text: '', offset: 0, nextOffset: 0, length: 0 })
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

describe('stalled turns in the Workspace', () => {
  it('passes only a well-formed stalled report through', () => {
    const frame = stalled => JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'working', source: 'hooks', stalled })
    expect(parseActivityFrame(frame({ minutes: 21, reason: 'no_progress' })).stalled).toEqual({ minutes: 21, reason: 'no_progress' })
    expect(parseActivityFrame(frame({ minutes: 3, reason: 'reconnecting' })).stalled).toEqual({ minutes: 3, reason: 'reconnecting' })
    expect(parseActivityFrame(frame({ minutes: 3, reason: '<b>x</b>' })).stalled).toEqual({ minutes: 3, reason: 'no_progress' })
    for (const bad of [null, {}, { minutes: '3' }, { minutes: -1 }, { minutes: 1.5 }]) expect(parseActivityFrame(frame(bad)).stalled).toBeUndefined()
  })

  it('replaces "Working…" with the stall and interrupts through the driver key', async () => {
    const wrapper = view(interactive)
    await flushPromises()
    mocks.activity({ state: 'working', source: 'hooks' })
    await flushPromises()
    expect(wrapper.get('[data-workspace-activity]').text()).toBe('Working…')
    expect(wrapper.find('[data-stalled]').exists()).toBe(false)

    mocks.activity({ state: 'working', source: 'hooks', stalled: { minutes: 20, reason: 'no_progress' } })
    await flushPromises()
    expect(wrapper.get('[data-stalled]').text()).toContain('No progress for 20 min: the turn may be stuck.')
    expect(wrapper.get('[data-stalled]').text()).toContain('Interrupting it sends the messages waiting for the agent.')
    await wrapper.get('[data-stalled-interrupt]').trigger('click')
    expect(mocks.interruptQueue).toHaveBeenCalledTimes(1)

    mocks.activity({ state: 'working', source: 'hooks', stalled: { minutes: 4, reason: 'reconnecting' } })
    await flushPromises()
    expect(wrapper.get('[data-stalled]').text()).toContain('Waiting for the network for 4 min')

    mocks.activity({ state: 'idle', source: 'hooks' })
    await flushPromises()
    expect(wrapper.find('[data-stalled]').exists()).toBe(false)
  })

  it('offers no Interrupt to a viewer who cannot write', async () => {
    const wrapper = view({ ...interactive, accessRole: 'Viewer' })
    await flushPromises()
    mocks.activity({ state: 'working', source: 'hooks', stalled: { minutes: 25, reason: 'no_progress' } })
    await flushPromises()
    expect(wrapper.get('[data-stalled]').text()).toBe('No progress for 25 min: the turn may be stuck.')
    expect(wrapper.find('[data-stalled-interrupt]').exists()).toBe(false)
  })
})
