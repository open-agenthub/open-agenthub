// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, onMounted } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from '../TerminalView.vue'
import WorkspaceComposer from './WorkspaceComposer.vue'
import ConversationTimeline from './ConversationTimeline.vue'
import { workspaceDrafts } from '../../lib/session-drafts.js'

const mocks = vi.hoisted(() => ({
  api: {
    getConversation: vi.fn(), listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]), decidePermission: vi.fn()
  },
  shared: vi.fn(), send: vi.fn(), stop: vi.fn(), mounted: vi.fn(), holdConnection: false, status: null
}))
vi.mock('../../api.js', () => ({ api: mocks.api, getSharedConversation: mocks.shared }))
vi.mock('../../lib/mermaid.js', () => ({ renderMermaidBlocks: vi.fn() }))

const session = { id: 's1', agent: 'Codex', title: 'Build a feature', phase: 'Running', mode: 'Interactive', accessRole: 'Owner' }
const page = (entries, extra = {}) => ({ source: 'native', entries, text: '', offset: 0, nextOffset: entries.length, length: entries.length, ...extra })
const entry = (role, text, tool) => ({ role, text, tool })
const wrappers = []
function view(props = {}) {
  const wrapper = mount(TerminalView, {
    props: { session, initialUi: 'terminal', ...props },
    global: { stubs: {
      SessionWorkspace: { template: '<div><slot /></div>' }, ShareSessionDialog: true,
      TerminalPane: defineComponent({
        props: ['kind'], emits: ['status', 'activity', 'menu'],
        setup(props, { emit, expose }) {
          expose({ submitMessage: mocks.send, interruptAgent: mocks.stop })
          onMounted(() => {
            mocks.mounted(props.kind)
            if (props.kind === 'agent') mocks.status = value => emit('status', value)
            if (mocks.holdConnection) return
            emit('status', 'connected')
            // The real agent answers the activity subscription at once; until it has, the
            // Workspace counts the agent as still starting (#241).
            if (props.kind === 'agent') emit('activity', { state: 'idle', source: 'hooks' })
          })
          return () => null
        }
      })
    } }
  })
  wrappers.push(wrapper)
  return wrapper
}
async function workspace(wrapper) {
  await wrapper.get('[data-open-workspace]').trigger('click')
  await flushPromises()
}
beforeEach(() => {
  localStorage.clear()
  workspaceDrafts.clear()
  vi.clearAllMocks()
  mocks.holdConnection = false
  mocks.send.mockResolvedValue(undefined)
  mocks.api.getConversation.mockResolvedValue(page([
    entry('user', 'Build a feature'), entry('assistant', 'Done **successfully**.'),
    entry('tool', '{"command":"npm test"}', 'Bash'), entry('result', 'All tests passed')
  ]))
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

describe('integrated workspace', () => {
  it('queues a message while the session starts and sends it through the agent terminal once connected (#115)', async () => {
    mocks.holdConnection = true
    const wrapper = view({ session: { ...session, phase: 'Pending' }, initialUi: 'workspace' })
    await flushPromises()
    const textarea = wrapper.get('[data-workspace-composer] textarea')
    expect(textarea.element.disabled).toBe(false)
    await textarea.setValue('Start with the tests')
    await wrapper.get('[data-workspace-composer]').trigger('submit')
    await flushPromises()
    expect(mocks.send).not.toHaveBeenCalled()
    expect(wrapper.get('[data-queued-message]').text()).toContain('Start with the tests')

    await wrapper.setProps({ session: { ...session, phase: 'Running' } })
    mocks.status('connected')
    await flushPromises()
    expect(mocks.send).toHaveBeenCalledTimes(1)
    expect(mocks.send).toHaveBeenCalledWith('Start with the tests', undefined)
    expect(wrapper.find('[data-queued-message]').exists()).toBe(false)
  })


  it('opens the saved surface and permits an explicit creation choice without changing the preference', async () => {
    localStorage.setItem('agenthub.preferredUi', 'terminal')
    const terminal = view({ initialUi: null })
    expect(terminal.get('.tabs button.on').text()).toBe('Terminal')
    const workspaceView = view({ initialUi: 'workspace' })
    await flushPromises()
    expect(workspaceView.get('.tabs button.on').text()).toBe('Workspace')
    expect(localStorage.getItem('agenthub.preferredUi')).toBe('terminal')
    localStorage.setItem('agenthub.preferredUi', 'workspace')
    const preferred = view({ initialUi: null })
    await flushPromises()
    expect(preferred.get('.tabs button.on').text()).toBe('Workspace')
  })
  it('renders native roles, Markdown and compact activity, sends and stops through the existing terminal', async () => {
    const wrapper = view()
    await workspace(wrapper)
    expect(wrapper.get('[data-workspace-role=assistant]').html()).toContain('<strong>successfully</strong>')
    expect(wrapper.findAll('[data-work-log]')).toHaveLength(2)
    expect(wrapper.get('[data-work-log]').text()).toContain('Bash')
    await wrapper.get('[data-workspace-composer] textarea').setValue('Now improve it')
    await wrapper.get('[data-workspace-composer]').trigger('submit')
    await flushPromises()
    expect(mocks.send).toHaveBeenCalledWith('Now improve it', undefined)
    await wrapper.get('[aria-label="Interrupt agent"]').trigger('click')
    expect(mocks.stop).toHaveBeenCalledOnce()
    await wrapper.get('[data-drawer-agent]').trigger('click')
    await wrapper.get('[data-workspace-composer] textarea').setValue('Keep across views')
    await wrapper.findAll('.tabs button').find(button => button.text() === 'Terminal').trigger('click')
    await workspace(wrapper)
    expect(mocks.mounted).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-workspace-composer] textarea').element.value).toBe('Keep across views')
  })

  it('quotes an actual message into the composer', async () => {
    const wrapper = view()
    await workspace(wrapper)
    await wrapper.findAll('[aria-label="Quote message"]')[0].trigger('click')
    expect(wrapper.get('textarea').element.value).toBe('> Build a feature\n\n')
  })

  it('does not expose sending, stop, or shell to a shared viewer', async () => {
    mocks.shared.mockResolvedValue(page([entry('assistant', 'Shared history')]))
    const wrapper = view({ session: { ...session, accessRole: 'Viewer' }, sharedToken: 'share-token' })
    await workspace(wrapper)
    expect(mocks.shared).toHaveBeenCalledWith('share-token', -1)
    expect(wrapper.text()).toContain('Shared history')
    expect(wrapper.find('[data-workspace-composer]').exists()).toBe(false)
    expect(wrapper.find('[data-drawer-shell]').exists()).toBe(false)
    expect(mocks.send).not.toHaveBeenCalled()
  })

  it('opens a recent window and loads earlier pages on demand after completion', async () => {
    mocks.api.getConversation
      .mockResolvedValueOnce(page([entry('tool', 'Last page')], { offset: 1000, nextOffset: 1001, length: 1001 }))
      .mockResolvedValueOnce(page(Array.from({ length: 500 }, (_, i) => entry('assistant', 'Earlier page ' + i)), { offset: 500, nextOffset: 1000, length: 1001 }))
    const wrapper = view({ session: { ...session, phase: 'Succeeded' } })
    await workspace(wrapper)
    expect(mocks.api.getConversation).toHaveBeenCalledExactlyOnceWith('s1', -1)
    expect(wrapper.text()).toContain('Last page')
    expect(wrapper.text()).not.toContain('Earlier page')
    const expanded = wrapper.get('[data-work-log]').element
    expanded.open = true
    await wrapper.get('[data-load-earlier]').trigger('click')
    await flushPromises()
    expect(mocks.api.getConversation).toHaveBeenNthCalledWith(2, 's1', 500, 500)
    expect(wrapper.get('[data-work-log]').element).toBe(expanded)
    expect(wrapper.get('[data-work-log]').element.open).toBe(true)
    expect(wrapper.text()).toContain('Earlier page')
    expect(wrapper.text()).toContain('Last page')
    expect(wrapper.find('textarea').exists()).toBe(false)
  })

  it('replaces input at the bottom while stopped and restores the saved draft on resume', async () => {
    const wrapper = view({ initialUi: 'workspace' })
    await flushPromises()
    await wrapper.get('textarea').setValue('Keep this draft')
    await wrapper.setProps({ session: { ...session, phase: 'Paused', canResume: true } })
    expect(wrapper.find('textarea').exists()).toBe(false)
    const footer = wrapper.get('[data-session-input-state]')
    expect(footer.element.parentElement.lastElementChild).toBe(footer.element)
    expect(footer.text()).toContain('Session is paused')
    await footer.get('[data-stopped-resume]').trigger('click')
    expect(wrapper.emitted('resume')).toEqual([['s1']])
    await wrapper.setProps({ session })
    expect(wrapper.find('[data-session-input-state]').exists()).toBe(false)
    expect(wrapper.get('textarea').element.value).toBe('Keep this draft')
  })

  it('shows working separately from an approval, question or menu and clears it when stopped', async () => {
    const wrapper = view({ initialUi: 'workspace' })
    await flushPromises()
    const terminal = wrapper.findComponent({ ref: 'agentTerminal' })
    terminal.vm.$emit('activity', { state: 'working', source: 'hooks' })
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[data-workspace-activity]').text()).toBe('Working…')
    terminal.vm.$emit('activity', { state: 'waiting', source: 'hooks', reason: 'approval' })
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[data-workspace-activity]').text()).toBe('Waiting for your approval…')
    // #252: a question the CLI shows in its permission flow is not an approval.
    terminal.vm.$emit('activity', { state: 'waiting', source: 'hooks', reason: 'answer' })
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[data-workspace-activity]').text()).toBe('Waiting for your answer…')
    terminal.vm.$emit('activity', { state: 'waiting', source: 'hooks' })
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[data-workspace-activity]').text()).toBe('Waiting for your answer…')
    // #343: the hub's questionPending means a turn ended (the relays' "question"), not that a
    // question is open; Claude Code raises it a minute after every answer.
    terminal.vm.$emit('activity', { state: 'working', source: 'hooks' })
    await wrapper.setProps({ session: { ...session, questionPending: true } })
    expect(wrapper.get('[data-workspace-activity]').text()).toBe('Working…')
    terminal.vm.$emit('activity', { state: 'idle', source: 'hooks' })
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[data-workspace-activity]').exists()).toBe(false)
    await wrapper.setProps({ session: { ...session, phase: 'Paused' } })
    expect(wrapper.find('[data-workspace-activity]').exists()).toBe(false)
  })

  it('offers retry on history failure and does not manufacture an empty successful transcript', async () => {
    mocks.api.getConversation.mockRejectedValueOnce(new Error('offline'))
    const wrapper = view()
    await workspace(wrapper)
    expect(wrapper.get('[role=alert]').text()).toContain('Could not load')
    await wrapper.get('[role=alert] button').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('successfully')
  })

  it('never lets an old history response replace the selected session', async () => {
    let resolve
    mocks.api.getConversation.mockReturnValueOnce(new Promise(done => { resolve = done }))
    const wrapper = view()
    await wrapper.get('[data-open-workspace]').trigger('click')
    await wrapper.setProps({ session: { ...session, id: 's2' } })
    await flushPromises()
    resolve(page([entry('assistant', 'Old private message')]))
    await flushPromises()
    expect(wrapper.text()).not.toContain('Old private message')
    expect(wrapper.text()).toContain('successfully')
  })
})

describe('workspace interaction boundaries', () => {
  it('restores unsent text after the application remounts a keyed session view', async () => {
    const first = mount(WorkspaceComposer, { props: { sessionId: 'first', enabled: true } })
    await first.get('textarea').setValue('Unsent task')
    first.unmount()
    const second = mount(WorkspaceComposer, { props: { sessionId: 'second', enabled: true } })
    expect(second.get('textarea').element.value).toBe('')
    second.unmount()
    const restored = mount(WorkspaceComposer, { props: { sessionId: 'first', enabled: true } })
    wrappers.push(restored)
    expect(restored.get('textarea').element.value).toBe('Unsent task')
  })
  it('retains a failed draft and does not submit while an IME composition is active', async () => {
    const send = vi.fn().mockRejectedValue(new Error('Connection changed'))
    const wrapper = mount(WorkspaceComposer, { props: { sessionId: 's1', enabled: true, send } })
    wrappers.push(wrapper)
    await wrapper.get('textarea').setValue('Keep this draft')
    await wrapper.get('textarea').trigger('keydown', { key: 'Enter', isComposing: true })
    expect(send).not.toHaveBeenCalled()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('textarea').element.value).toBe('Keep this draft')
    expect(wrapper.get('[role=alert]').text()).toContain('Connection changed')
  })

  it('keeps drafts per session and ignores the completion of a send from an old session', async () => {
    let finish
    const wrapper = mount(WorkspaceComposer, { props: {
      sessionId: 's1', enabled: true, send: () => new Promise(resolve => { finish = resolve })
    } })
    wrappers.push(wrapper)
    await wrapper.get('textarea').setValue('First draft')
    await wrapper.get('form').trigger('submit')
    await wrapper.setProps({ sessionId: 's2' })
    await wrapper.get('textarea').setValue('Second draft')
    finish()
    await flushPromises()
    expect(wrapper.get('textarea').element.value).toBe('Second draft')
    await wrapper.setProps({ sessionId: 's1' })
    expect(wrapper.get('textarea').element.value).toBe('First draft')
  })

  it('searches message and tool contents while escaping untrusted output', async () => {
    const wrapper = mount(ConversationTimeline, { props: { items: [
      { role: 'assistant', label: 'Agent', text: '<img src=x onerror=alert(1)> **Safe**' },
      { role: 'tool', label: 'Tool · Bash', text: 'npm test' }
    ] } })
    wrappers.push(wrapper)
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.html()).toContain('<strong>Safe</strong>')
    await wrapper.get('input[type=search]').setValue('npm test')
    expect(wrapper.find('[data-workspace-role]').exists()).toBe(false)
    expect(wrapper.get('[data-work-log]').text()).toContain('npm test')
  })
})
