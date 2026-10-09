// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn(),
    getConversation: vi.fn(),
    listPermissions: vi.fn().mockResolvedValue([]),
    decidePermission: vi.fn()
  },
  getSharedTranscript: vi.fn(),
  getSharedConversation: vi.fn()
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  getSharedTranscript: mocks.getSharedTranscript,
  getSharedConversation: mocks.getSharedConversation
}))

const session = {
  id: 'terminal-1',
  title: 'Terminal session',
  phase: 'Succeeded',
  mode: 'Interactive'
}

const scrollbackPage = text => ({
  source: 'scrollback', entries: [], text, offset: 0, nextOffset: text.length, length: text.length, running: false
})
const nativePage = entries => ({
  source: 'native', entries, text: '', offset: 0, nextOffset: entries.length, length: entries.length, running: false
})

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

describe('transcript tab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.getConversation.mockResolvedValue(scrollbackPage(''))
    mocks.getSharedConversation.mockResolvedValue(scrollbackPage(''))
  })

  it('renders the provider conversation as role-labelled turns without terminal heuristics', async () => {
    mocks.api.getConversation.mockResolvedValue(nativePage([
      { role: 'user', text: 'Fix the build', at: null },
      { role: 'assistant', text: 'Looking.', at: null },
      // Short enough that the terminal heuristics would have dropped it as spinner noise.
      { role: 'tool', text: 'ls', at: null, tool: 'Bash' },
      { role: 'result', text: 'ok', at: null },
      { role: 'assistant', text: 'Done.', at: null }
    ]))
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(mocks.api.getConversation).toHaveBeenCalledWith('terminal-1', undefined)
    expect(wrapper.find('.transcript-list').attributes('data-transcript-source')).toBe('native')
    expect(wrapper.findAll('.transcript-label').map(item => item.text()))
      .toEqual(['User', 'Agent', 'Tool · Bash', 'Result', 'Agent'])
    expect(wrapper.findAll('.transcript-bubble pre').map(item => item.text()))
      .toEqual(['Fix the build', 'Looking.', 'ls', 'ok', 'Done.'])
    expect(wrapper.findAll('.transcript-bubble').map(item => item.attributes('data-transcript-role')))
      .toEqual(['user', 'assistant', 'tool', 'result', 'assistant'])
  })

  it('falls back to neutral terminal bubbles for a session without a native transcript', async () => {
    mocks.api.getConversation.mockResolvedValue(scrollbackPage('first\n \n\t\nsecond'))
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.find('.transcript-list').attributes('data-transcript-source')).toBe('scrollback')
    expect(wrapper.findAll('.transcript-bubble').map(item => item.find('pre').text())).toEqual([
      'first\n\nsecond'
    ])
    expect(wrapper.findAll('.transcript-label').map(item => item.text())).toEqual(['Terminal'])
  })

  it('uses the same rendering for shared transcripts', async () => {
    mocks.getSharedConversation.mockResolvedValue(nativePage([{ role: 'user', text: 'shared hello', at: null }]))
    const wrapper = mountView({ sharedToken: 'shared-token' })

    await openTranscript(wrapper)

    expect(mocks.getSharedConversation).toHaveBeenCalledWith('shared-token', undefined)
    expect(mocks.api.getConversation).not.toHaveBeenCalled()
    expect(wrapper.findAll('.transcript-bubble pre').map(item => item.text())).toEqual(['shared hello'])
  })

  it('keeps the existing empty transcript state', async () => {
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.find('.transcript-state').text()).toBe('[no saved transcript]')
    expect(wrapper.findAll('.transcript-bubble')).toHaveLength(0)
  })

  it('shows the empty state rather than an error when the request fails', async () => {
    mocks.api.getConversation.mockRejectedValue(new Error('503'))
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.find('.transcript-state').text()).toBe('[no saved transcript]')
  })

  it('shows loading while the transcript request is pending', async () => {
    let resolveTranscript
    mocks.api.getConversation.mockImplementation(() => new Promise(resolve => { resolveTranscript = resolve }))
    const wrapper = mountView()
    const button = wrapper.findAll('.tabs button').find(item => item.text() === 'Transcript')

    await button.trigger('click')

    expect(wrapper.find('.transcript-state').text()).toBe('Loading…')

    resolveTranscript(scrollbackPage('ready'))
    await flushPromises()
    expect(wrapper.find('.transcript-bubble pre').text()).toBe('ready')
  })
})
