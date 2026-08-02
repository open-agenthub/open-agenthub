// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn(),
    listPermissions: vi.fn().mockResolvedValue([]),
    decidePermission: vi.fn()
  },
  getSharedTranscript: vi.fn()
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  getSharedTranscript: mocks.getSharedTranscript
}))

const session = {
  id: 'terminal-1',
  title: 'Terminal session',
  phase: 'Succeeded',
  mode: 'Interactive'
}

function mountView(props = {}) {
  return mount(TerminalView, {
    props: { session, ...props },
    global: {
      stubs: {
        TerminalPane: true,
        ShareSessionDialog: true,
        SessionWorkspace: { template: '<div><slot /></div>' }
      }
    }
  })
}

async function openTranscript(wrapper) {
  const button = wrapper.findAll('.tabs button').find(item => item.text() === 'Transcript')
  await button.trigger('click')
  await flushPromises()
}

describe('terminal transcript bubbles', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getTranscript.mockResolvedValue('')
    mocks.getSharedTranscript.mockResolvedValue('')
  })

  it('renders owner transcript blocks in source order as neutral terminal bubbles', async () => {
    mocks.api.getTranscript.mockResolvedValue('first\n \n\t\nsecond')
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(mocks.api.getTranscript).toHaveBeenCalledWith('terminal-1')
    expect(wrapper.findAll('.transcript-bubble').map(item => item.find('pre').text())).toEqual(['first', 'second'])
    expect(wrapper.findAll('.transcript-label').map(item => item.text())).toEqual(['Terminal', 'Terminal'])
  })

  it('uses the same bubble rendering for shared transcripts', async () => {
    mocks.getSharedTranscript.mockResolvedValue('shared first\n\n\nshared second')
    const wrapper = mountView({ sharedToken: 'shared-token' })

    await openTranscript(wrapper)

    expect(mocks.getSharedTranscript).toHaveBeenCalledWith('shared-token')
    expect(wrapper.findAll('.transcript-bubble').map(item => item.find('pre').text())).toEqual(['shared first', 'shared second'])
  })

  it('keeps the existing empty transcript state', async () => {
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.find('.transcript-state').text()).toBe('[no saved transcript]')
    expect(wrapper.findAll('.transcript-bubble')).toHaveLength(0)
  })

  it('shows loading while the transcript request is pending', async () => {
    let resolveTranscript
    mocks.api.getTranscript.mockImplementation(() => new Promise(resolve => { resolveTranscript = resolve }))
    const wrapper = mountView()
    const button = wrapper.findAll('.tabs button').find(item => item.text() === 'Transcript')

    await button.trigger('click')

    expect(wrapper.find('.transcript-state').text()).toBe('Loading…')

    resolveTranscript('ready')
    await flushPromises()
    expect(wrapper.find('.transcript-bubble pre').text()).toBe('ready')
  })
})
