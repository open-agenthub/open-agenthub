// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
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

// The saved conversation is read in the Workspace (#113 removed the Transcript tab that showed
// the same page a second time), so these tests open the Workspace and read its timeline.
function mountView(props = {}) {
  return mount(TerminalView, {
    props: { session, initialUi: 'workspace', ...props },
    global: {
      stubs: {
        TerminalPane: true,
        ShareSessionDialog: true,
        SessionWorkspace: { template: '<div><slot /></div>' }
      }
    }
  })
}

async function openTranscript() {
  await flushPromises()
}
// Turns in document order: messages and the tool/result work logs between them.
const turns = wrapper => wrapper.findAll('[data-workspace-role], [data-work-log]')
const texts = wrapper => turns(wrapper).map(item => item.find('.md, pre').text())

describe('saved conversation in the Workspace', () => {
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

    expect(mocks.api.getConversation).toHaveBeenCalledWith('terminal-1', -1)
    expect(wrapper.get('.conversation-toolbar').text()).toContain('Conversation')
    expect(turns(wrapper).map(item => item.find('.message-label, .work-label').text()))
      .toEqual(['User', 'Agent', 'Tool · Bash', 'Result', 'Agent'])
    expect(texts(wrapper)).toEqual(['Fix the build', 'Looking.', 'ls', 'ok', 'Done.'])
  })

  it('renders what the person and the model wrote as markdown, tool output verbatim', async () => {
    mocks.api.getConversation.mockResolvedValue(nativePage([
      { role: 'user', text: 'Fix **the build** in `ci.yml`', at: null },
      { role: 'assistant', text: '## Plan\n\n- one\n- two\n\n```sh\nnpm test\n```', at: null },
      { role: 'tool', text: '**not** markdown', at: null, tool: 'Bash' },
      { role: 'result', text: '<b>escaped</b>', at: null }
    ]))
    const wrapper = mountView()

    await openTranscript(wrapper)

    const prose = wrapper.findAll('[data-workspace-role] .md')
    expect(prose).toHaveLength(2)
    expect(prose[0].find('strong').text()).toBe('the build')
    expect(prose[0].find('code').text()).toBe('ci.yml')
    // The renderer lowers headings by two levels so agent text never outranks the page's own.
    expect(prose[1].find('h4').text()).toBe('Plan')
    expect(prose[1].findAll('li').map(item => item.text())).toEqual(['one', 'two'])
    expect(prose[1].find('pre code').text()).toBe('npm test')

    const verbatim = wrapper.findAll('[data-work-log] pre')
    expect(verbatim.map(item => item.text())).toEqual(['**not** markdown', '<b>escaped</b>'])
    expect(wrapper.find('[data-work-log] b').exists()).toBe(false)
  })

  it('falls back to neutral terminal bubbles for a session without a native transcript', async () => {
    mocks.api.getConversation.mockResolvedValue(scrollbackPage('first\n \n\t\nsecond'))
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.get('.conversation-toolbar').text()).toContain('Terminal history')
    expect(turns(wrapper).map(item => item.find('pre').text())).toEqual(['first\n\nsecond'])
    expect(wrapper.findAll('.message-label').map(item => item.text())).toEqual(['Terminal'])
  })

  it('uses the same rendering for shared transcripts', async () => {
    mocks.getSharedConversation.mockResolvedValue(nativePage([{ role: 'user', text: 'shared hello', at: null }]))
    const wrapper = mountView({ sharedToken: 'shared-token' })

    await openTranscript(wrapper)

    expect(mocks.getSharedConversation).toHaveBeenCalledWith('shared-token', -1)
    expect(mocks.api.getConversation).not.toHaveBeenCalled()
    expect(texts(wrapper)).toEqual(['shared hello'])
  })

  // A finished run has no composer, so the Workspace's "give your agent a task" invitation
  // would point at nothing; it says that nothing was saved, as the Transcript tab used to.
  it('says there is no saved conversation for a finished session', async () => {
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.get('[data-conversation-empty]').text()).toBe('No saved conversation.')
    expect(turns(wrapper)).toHaveLength(0)
  })

  it('shows a retryable error instead of reporting missing history when the request fails', async () => {
    mocks.api.getConversation.mockRejectedValue(new Error('503'))
    const wrapper = mountView()

    await openTranscript(wrapper)

    expect(wrapper.find('[role=alert]').text()).toContain('Could not load the conversation.')
    expect(wrapper.find('[role=alert] button').text()).toBe('Retry')
  })

  describe('while the session runs', () => {
    const running = { ...session, phase: 'Running' }
    const entry = text => ({ role: 'assistant', text, at: null })

    beforeEach(() => { vi.useFakeTimers() })
    afterEach(() => { vi.useRealTimers() })

    it('polls from the cursor and appends what is new', async () => {
      mocks.api.getConversation
        .mockResolvedValueOnce({ ...nativePage([entry('first')]), running: true })
        .mockResolvedValueOnce({ source: 'native', entries: [entry('second')], text: '', offset: 1, nextOffset: 2, length: 2, running: true })
        .mockResolvedValueOnce({ source: 'native', entries: [], text: '', offset: 2, nextOffset: 2, length: 2, running: true })
      const wrapper = mountView({ session: running })
      await openTranscript(wrapper)
      expect(texts(wrapper)).toEqual(['first'])

      await vi.advanceTimersByTimeAsync(1000)
      await flushPromises()

      expect(mocks.api.getConversation).toHaveBeenLastCalledWith('terminal-1', 1)
      expect(texts(wrapper)).toEqual(['first', 'second'])

      // Nothing new: the cursor stays and nothing is duplicated.
      await vi.advanceTimersByTimeAsync(1000)
      await flushPromises()
      expect(mocks.api.getConversation).toHaveBeenLastCalledWith('terminal-1', 2)
      expect(turns(wrapper)).toHaveLength(2)
    })

    it('appends scrollback text for sessions without a native transcript', async () => {
      mocks.api.getConversation
        .mockResolvedValueOnce({ ...scrollbackPage('hello world'), running: true })
        .mockResolvedValueOnce({ source: 'scrollback', entries: [], text: ' and more', offset: 11, nextOffset: 20, length: 20, running: true })
      const wrapper = mountView({ session: running })
      await openTranscript(wrapper)

      await vi.advanceTimersByTimeAsync(1000)
      await flushPromises()

      expect(mocks.api.getConversation).toHaveBeenLastCalledWith('terminal-1', 11)
      expect(texts(wrapper)).toEqual(['hello world and more'])
    })

    it('reloads from the start when the cursor went stale', async () => {
      mocks.api.getConversation
        .mockResolvedValueOnce({ ...nativePage([entry('first'), entry('second')]), running: true })
        // The server's copy shrank under the cursor: a clamped, shorter page comes back.
        .mockResolvedValueOnce({ source: 'native', entries: [], text: '', offset: 1, nextOffset: 1, length: 1, running: true })
        .mockResolvedValueOnce({ ...nativePage([entry('only')]), running: true })
      const wrapper = mountView({ session: running })
      await openTranscript(wrapper)

      await vi.advanceTimersByTimeAsync(1000)
      await flushPromises()

      expect(mocks.api.getConversation).toHaveBeenLastCalledWith('terminal-1', -1)
      expect(texts(wrapper)).toEqual(['only'])
    })

    it('does not poll a finished session or while the Terminal view is open', async () => {
      mocks.api.getConversation.mockResolvedValue(nativePage([entry('first')]))
      const finished = mountView()
      await openTranscript(finished)
      await vi.advanceTimersByTimeAsync(8000)
      await flushPromises()
      expect(mocks.api.getConversation).toHaveBeenCalledTimes(1)

      mocks.api.getConversation.mockClear()
      mountView({ session: running, initialUi: 'terminal' })
      await vi.advanceTimersByTimeAsync(8000)
      await flushPromises()
      expect(mocks.api.getConversation).not.toHaveBeenCalled()
    })

    it('follows a running session every second and skips polls while the browser tab is hidden', async () => {
      mocks.api.getConversation.mockResolvedValue({ source: 'native', entries: [], text: '', offset: 1, nextOffset: 1, length: 1, running: true })
      mocks.api.getConversation.mockResolvedValueOnce({ ...nativePage([entry('first')]), running: true })
      const wrapper = mountView({ session: running })
      await openTranscript()
      const calls = () => mocks.api.getConversation.mock.calls.length
      const before = calls()

      await vi.advanceTimersByTimeAsync(1000)
      await flushPromises()
      expect(calls()).toBe(before + 1)

      const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
      await vi.advanceTimersByTimeAsync(3000)
      await flushPromises()
      expect(calls()).toBe(before + 1)

      visibility.mockRestore()
      await vi.advanceTimersByTimeAsync(1000)
      await flushPromises()
      expect(calls()).toBe(before + 2)
      wrapper.unmount()
    })

    it('fetches once more when the session ends, for the final upload', async () => {
      mocks.api.getConversation
        .mockResolvedValueOnce({ ...nativePage([entry('working')]), running: true })
        .mockResolvedValueOnce({ source: 'native', entries: [entry('done')], text: '', offset: 1, nextOffset: 2, length: 2, running: false })
      const wrapper = mountView({ session: running })
      await openTranscript(wrapper)

      await wrapper.setProps({ session: { ...running, phase: 'Succeeded' } })
      await flushPromises()

      expect(texts(wrapper)).toEqual(['working', 'done'])
    })
  })

  it('shows loading while the conversation request is pending', async () => {
    let resolveTranscript
    mocks.api.getConversation.mockImplementation(() => new Promise(resolve => { resolveTranscript = resolve }))
    const wrapper = mountView()
    await wrapper.vm.$nextTick()

    expect(wrapper.get('[data-workspace-conversation] [role=status]').text()).toBe('Loading conversation…')

    resolveTranscript(scrollbackPage('ready'))
    await flushPromises()
    expect(texts(wrapper)).toEqual(['ready'])
  })
})

// A just-created session has no transcript yet; that is the expected state while its pod is
// pending and its agent starts, not a failure to load (#241).
describe('a session that is starting', () => {
  const entry = text => ({ role: 'assistant', text, at: null })
  const starting = wrapper => wrapper.find('[data-conversation-starting]')

  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers()
    mocks.api.getConversation.mockResolvedValue({ ...scrollbackPage(''), running: true })
  })
  afterEach(() => { vi.useRealTimers() })

  it('shows the starting state while the pod is pending and switches to the conversation on its first entry', async () => {
    const wrapper = mountView({ session: { ...session, phase: 'Pending' } })
    await flushPromises()

    expect(starting(wrapper).text()).toContain('Session starting')
    expect(starting(wrapper).text()).toContain('Waiting for the session’s pod to start…')
    expect(starting(wrapper).find('.starting-mark').exists()).toBe(true)
    expect(wrapper.find('[role=alert]').exists()).toBe(false)

    await wrapper.setProps({ session: { ...session, phase: 'Running' } })
    expect(starting(wrapper).text()).toContain('Starting the agent…')

    mocks.api.getConversation.mockResolvedValue({ ...nativePage([entry('Ready.')]), running: true })
    await vi.advanceTimersByTimeAsync(1000)
    await flushPromises()

    expect(starting(wrapper).exists()).toBe(false)
    expect(texts(wrapper)).toEqual(['Ready.'])
  })

  it('keeps starting instead of an error while there is nothing to load yet, and recovers on the next poll', async () => {
    mocks.api.getConversation.mockRejectedValueOnce(new Error('503'))
    const wrapper = mountView({ session: { ...session, phase: 'Pending' } })
    await flushPromises()

    expect(starting(wrapper).exists()).toBe(true)
    expect(wrapper.find('[role=alert]').exists()).toBe(false)

    await vi.advanceTimersByTimeAsync(1000)
    await flushPromises()
    expect(mocks.api.getConversation).toHaveBeenCalledTimes(2)
    expect(starting(wrapper).exists()).toBe(true)
  })

  it('stays starting for an autonomous run until its first output', async () => {
    const wrapper = mountView({ session: { ...session, phase: 'Running', mode: 'Autonomous' } })
    await flushPromises()
    const pane = wrapper.findComponent({ name: 'TerminalPane' })
    pane.vm.$emit('status', 'connected')
    pane.vm.$emit('activity', { state: 'idle', source: 'hooks' })
    await flushPromises()

    expect(starting(wrapper).text()).toContain('The agent is starting its task…')
  })

  // The hub accepts the terminal socket before it reaches the pod, so an open socket with no
  // answer from the agent is still the agent starting, not an agent waiting for a task.
  it('stays starting while the terminal socket is open but the agent has not answered', async () => {
    const wrapper = mountView({ session: { ...session, phase: 'Running' } })
    await flushPromises()
    wrapper.findComponent({ name: 'TerminalPane' }).vm.$emit('status', 'connected')
    await flushPromises()

    expect(starting(wrapper).text()).toContain('Starting the agent…')
  })

  it('invites a task once an interactive agent is connected', async () => {
    const wrapper = mountView({ session: { ...session, phase: 'Running' } })
    await flushPromises()
    const pane = wrapper.findComponent({ name: 'TerminalPane' })
    pane.vm.$emit('status', 'connected')
    pane.vm.$emit('activity', { state: 'idle', source: 'hooks' })
    await flushPromises()

    expect(starting(wrapper).exists()).toBe(false)
    expect(wrapper.text()).toContain('Your agent’s workspace')
  })

  it('keeps a real load error for a finished session', async () => {
    mocks.api.getConversation.mockRejectedValue(new Error('503'))
    const wrapper = mountView()
    await flushPromises()

    expect(starting(wrapper).exists()).toBe(false)
    expect(wrapper.find('[role=alert]').text()).toContain('Could not load the conversation.')
  })
})
