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
  shared: vi.fn(), send: vi.fn(), stop: vi.fn(), mounted: vi.fn()
}))
vi.mock('../../api.js', () => ({ api: mocks.api, getSharedConversation: mocks.shared }))
vi.mock('../../lib/mermaid.js', () => ({ renderMermaidBlocks: vi.fn() }))

const session = { id: 's1', agent: 'Codex', title: 'Build a feature', phase: 'Running', mode: 'Interactive', accessRole: 'Owner' }
const page = (entries, extra = {}) => ({ source: 'native', entries, text: '', offset: 0, nextOffset: entries.length, length: entries.length, ...extra })
const entry = (role, text, tool) => ({ role, text, tool })
const wrappers = []
function view(props = {}) {
  const wrapper = mount(TerminalView, {
    props: { session, ...props },
    global: { stubs: {
      SessionWorkspace: { template: '<div><slot /></div>' }, ShareSessionDialog: true,
      TerminalPane: defineComponent({
        props: ['kind'], emits: ['status'],
        setup(props, { emit, expose }) {
          expose({ submitMessage: mocks.send, interruptAgent: mocks.stop })
          onMounted(() => { mocks.mounted(props.kind); emit('status', 'connected') })
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
  workspaceDrafts.clear()
  vi.clearAllMocks()
  mocks.send.mockResolvedValue(undefined)
  mocks.api.getConversation.mockResolvedValue(page([
    entry('user', 'Build a feature'), entry('assistant', 'Done **successfully**.'),
    entry('tool', '{"command":"npm test"}', 'Bash'), entry('result', 'All tests passed')
  ]))
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })

describe('integrated workspace', () => {
  it('renders native roles, Markdown and compact activity, sends and stops through the existing terminal', async () => {
    const wrapper = view()
    await workspace(wrapper)
    expect(wrapper.get('[data-workspace-role=assistant]').html()).toContain('<strong>successfully</strong>')
    expect(wrapper.findAll('[data-work-log]')).toHaveLength(2)
    expect(wrapper.get('[data-work-log]').text()).toContain('Bash')
    await wrapper.get('[data-workspace-composer] textarea').setValue('Now improve it')
    await wrapper.get('[data-workspace-composer]').trigger('submit')
    await flushPromises()
    expect(mocks.send).toHaveBeenCalledWith('Now improve it')
    await wrapper.get('[aria-label="Interrupt agent"]').trigger('click')
    expect(mocks.stop).toHaveBeenCalledOnce()
    await wrapper.get('[data-drawer-agent]').trigger('click')
    await wrapper.get('[data-workspace-composer] textarea').setValue('Keep across views')
    await wrapper.findAll('.tabs button').find(button => button.text() === 'Agent').trigger('click')
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
    expect(mocks.shared).toHaveBeenCalledWith('share-token', undefined)
    expect(wrapper.text()).toContain('Shared history')
    expect(wrapper.find('[data-workspace-composer]').exists()).toBe(false)
    expect(wrapper.find('[data-drawer-shell]').exists()).toBe(false)
    expect(mocks.send).not.toHaveBeenCalled()
  })

  it('drains saved conversation pages even after a session has finished', async () => {
    mocks.api.getConversation
      .mockResolvedValueOnce(page([entry('assistant', 'First page')], { length: 2 }))
      .mockResolvedValueOnce(page([entry('assistant', 'Last page')], { offset: 1, nextOffset: 2, length: 2 }))
    const wrapper = view({ session: { ...session, phase: 'Succeeded' } })
    await workspace(wrapper)
    expect(mocks.api.getConversation).toHaveBeenNthCalledWith(2, 's1', 1)
    expect(wrapper.text()).toContain('Last page')
    expect(wrapper.get('textarea').attributes('disabled')).toBeDefined()
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
