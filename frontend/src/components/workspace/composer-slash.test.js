// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import WorkspaceComposer from './WorkspaceComposer.vue'
import { workspaceDrafts, workspaceQueues } from '../../lib/session-drafts.js'

// #169: slash commands are completed in the composer and sent to the CLI like any message.
const commands = [
  { name: 'compact', source: 'builtin', description: 'Free up context by summarizing' },
  { name: 'model', source: 'builtin', description: 'Set the AI model' },
  { name: 'deploy-check', source: 'custom', description: 'Check the deploy', argumentHint: '<env>' },
  { name: 'clear', source: 'builtin', description: 'Start a new session', effect: 'new-conversation' }
]
const wrappers = []
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  workspaceDrafts.clear()
  workspaceQueues.clear()
})

function composer(props = {}) {
  const wrapper = mount(WorkspaceComposer, {
    attachTo: document.body,
    props: { sessionId: 's1', enabled: true, send: vi.fn().mockResolvedValue(), loadCommands: vi.fn().mockResolvedValue({ commands }), ...props }
  })
  wrappers.push(wrapper)
  return wrapper
}

async function type(wrapper, value) {
  const textarea = wrapper.get('textarea')
  await textarea.setValue(value)
  textarea.element.setSelectionRange(value.length, value.length)
  await textarea.trigger('input')
  await flushPromises()
}

const options = wrapper => wrapper.findAll('[data-slash-option]').map(option => option.get('.slash-name').element.firstChild.textContent)
const key = (wrapper, name) => wrapper.get('textarea').trigger('keydown', { key: name })

describe('slash commands in the composer', () => {
  it('loads the list on the first "/" and filters it while typing', async () => {
    const loadCommands = vi.fn().mockResolvedValue({ commands })
    const wrapper = composer({ loadCommands })
    await type(wrapper, 'hello')
    expect(loadCommands).not.toHaveBeenCalled()
    await type(wrapper, '/')
    expect(loadCommands).toHaveBeenCalledTimes(1)
    expect(options(wrapper)).toEqual(['/clear', '/compact', '/model', '/deploy-check'])
    expect(wrapper.get('.slash-hint').text()).toBe('<env>')
    await type(wrapper, '/mo')
    expect(options(wrapper)).toEqual(['/model'])
    expect(loadCommands).toHaveBeenCalledTimes(1)
    expect(wrapper.get('textarea').attributes('aria-expanded')).toBe('true')
  })

  it('Tab and Enter complete the highlighted command; Enter on a complete command sends it', async () => {
    const send = vi.fn().mockResolvedValue()
    const wrapper = composer({ send })
    await type(wrapper, '/co')
    await key(wrapper, 'Tab')
    expect(wrapper.get('textarea').element.value).toBe('/compact ')
    expect(send).not.toHaveBeenCalled()

    await type(wrapper, '/mo')
    await key(wrapper, 'Enter')
    expect(wrapper.get('textarea').element.value).toBe('/model ')
    expect(send).not.toHaveBeenCalled()

    // The caret after the space is in the arguments: the menu is closed and Enter sends.
    await type(wrapper, '/model ')
    expect(wrapper.find('[data-slash-menu]').exists()).toBe(false)
    await key(wrapper, 'Enter')
    await flushPromises()
    expect(send).toHaveBeenCalledWith('/model ')

    await type(wrapper, '/model')
    await key(wrapper, 'Enter')
    await flushPromises()
    expect(send).toHaveBeenLastCalledWith('/model')
  })

  it('arrow keys move the highlight, a click picks, Escape dismisses', async () => {
    const wrapper = composer()
    await type(wrapper, '/')
    await key(wrapper, 'ArrowDown')
    await key(wrapper, 'ArrowDown')
    expect(wrapper.findAll('[data-slash-option]')[2].attributes('aria-selected')).toBe('true')
    await key(wrapper, 'ArrowUp')
    await key(wrapper, 'Tab')
    expect(wrapper.get('textarea').element.value).toBe('/compact ')

    await type(wrapper, '/dep')
    await wrapper.get('[data-slash-option]').trigger('mousedown')
    expect(wrapper.get('textarea').element.value).toBe('/deploy-check ')

    await type(wrapper, '/m')
    await key(wrapper, 'Escape')
    expect(wrapper.find('[data-slash-menu]').exists()).toBe(false)
    // Dismissed for that token only: typing on opens it again.
    await type(wrapper, '/mo')
    expect(options(wrapper)).toEqual(['/model'])
  })

  it('Enter without a menu sends unknown commands as typed', async () => {
    const send = vi.fn().mockResolvedValue()
    const wrapper = composer({ send })
    await type(wrapper, '/not-listed arg')
    await key(wrapper, 'Enter')
    await flushPromises()
    expect(send).toHaveBeenCalledWith('/not-listed arg')
  })

  it('warns before a command that switches the conversation', async () => {
    const wrapper = composer()
    await type(wrapper, '/')
    await type(wrapper, '/clear')
    expect(wrapper.get('[data-slash-warning]').text()).toContain('/clear switches the agent to another conversation')
    await type(wrapper, '/compact')
    expect(wrapper.find('[data-slash-warning]').exists()).toBe(false)
  })

  it('without a list (old runtime, failed request, no loader) the composer just sends', async () => {
    const send = vi.fn().mockResolvedValue()
    const failing = composer({ send, loadCommands: vi.fn().mockRejectedValue(new Error('503')) })
    await type(failing, '/mo')
    expect(failing.find('[data-slash-menu]').exists()).toBe(false)
    await key(failing, 'Enter')
    await flushPromises()
    expect(send).toHaveBeenCalledWith('/mo')

    const none = composer({ loadCommands: undefined, sessionId: 's2' })
    await type(none, '/mo')
    expect(none.find('[data-slash-menu]').exists()).toBe(false)
  })

  it('does not offer commands mid-message, where the CLI would not run them', async () => {
    const wrapper = composer()
    await type(wrapper, '/')
    await type(wrapper, 'please /mo')
    expect(wrapper.find('[data-slash-menu]').exists()).toBe(false)
  })
})
