// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import WorkspaceComposer from './WorkspaceComposer.vue'
import { workspaceDrafts, workspaceQueues } from '../../lib/session-drafts.js'

// #115: a message written while the session starts is queued and sent once the agent is ready.
const wrappers = []
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  workspaceDrafts.clear()
  workspaceQueues.clear()
})

function composer(props) {
  const wrapper = mount(WorkspaceComposer, { props: { sessionId: 's1', enabled: false, queueable: true, send: vi.fn().mockResolvedValue(), ...props } })
  wrappers.push(wrapper)
  return wrapper
}

async function queue(wrapper, text) {
  await wrapper.get('textarea').setValue(text)
  await wrapper.get('form').trigger('submit')
}

describe('composer queue while the session starts', () => {
  it('accepts a message while starting, shows it as queued, and sends it once in order when ready', async () => {
    const sent = []
    const send = vi.fn(async text => { sent.push(text) })
    const wrapper = composer({ send })
    expect(wrapper.get('textarea').element.disabled).toBe(false)
    expect(wrapper.get('button[type=submit]').text()).toContain('Queue')

    await queue(wrapper, 'first task')
    await queue(wrapper, 'second task')
    expect(send).not.toHaveBeenCalled()
    expect(wrapper.get('textarea').element.value).toBe('')
    const rows = wrapper.findAll('[data-queued-message]')
    expect(rows.map(row => row.get('.queued-text').text())).toEqual(['first task', 'second task'])
    expect(rows[0].text()).toContain('will be sent when the agent is ready')

    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(sent).toEqual(['first task', 'second task'])
    expect(wrapper.findAll('[data-queued-message]')).toHaveLength(0)

    // A reconnect later does not send anything a second time.
    await wrapper.setProps({ enabled: false, queueable: true })
    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(send).toHaveBeenCalledTimes(2)
  })

  it('sends the second message only after the first one was submitted', async () => {
    let finishFirst
    const send = vi.fn().mockImplementationOnce(() => new Promise(resolve => { finishFirst = resolve })).mockResolvedValue()
    const wrapper = composer({ send })
    await queue(wrapper, 'one')
    await queue(wrapper, 'two')
    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(send.mock.calls.map(call => call[0])).toEqual(['one'])
    finishFirst()
    await flushPromises()
    expect(send.mock.calls.map(call => call[0])).toEqual(['one', 'two'])
  })

  it('lets a queued message be edited or withdrawn before it goes out', async () => {
    const send = vi.fn().mockResolvedValue()
    const wrapper = composer({ send })
    await queue(wrapper, 'drop me')
    await queue(wrapper, 'fix me')
    await wrapper.findAll('[data-queued-withdraw]')[0].trigger('click')
    await wrapper.get('[data-queued-edit]').trigger('click')
    expect(wrapper.findAll('[data-queued-message]')).toHaveLength(0)
    expect(wrapper.get('textarea').element.value).toBe('fix me')

    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(send).not.toHaveBeenCalled()
  })

  it('puts the queue back into the draft when the session fails to start', async () => {
    const wrapper = composer()
    await queue(wrapper, 'first')
    await wrapper.get('textarea').setValue('still typing')
    await wrapper.setProps({ queueable: false })
    expect(wrapper.findAll('[data-queued-message]')).toHaveLength(0)
    expect(wrapper.get('textarea').element.value).toBe('first\n\nstill typing')
  })

  it('keeps a message queued when the agent drops before anything was written, and sends it on reconnect', async () => {
    const send = vi.fn()
      .mockRejectedValueOnce(Object.assign(new Error('Agent is disconnected'), { retryable: true }))
      .mockResolvedValue()
    const wrapper = composer({ send })
    await queue(wrapper, 'task')
    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(wrapper.findAll('[data-queued-message]')).toHaveLength(1)

    await wrapper.setProps({ enabled: false, queueable: true })
    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(send.mock.calls.map(call => call[0])).toEqual(['task', 'task'])
    expect(wrapper.findAll('[data-queued-message]')).toHaveLength(0)
  })

  it('returns a message to the draft when a send failed after it may have reached the terminal', async () => {
    const send = vi.fn().mockRejectedValue(new Error('Connection changed. Check the agent terminal before retrying your draft.'))
    const wrapper = composer({ send })
    await queue(wrapper, 'maybe sent')
    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(send).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('[data-queued-message]')).toHaveLength(0)
    expect(wrapper.get('textarea').element.value).toBe('maybe sent')
    expect(wrapper.get('[role=alert]').text()).toContain('Check the agent terminal')
  })

  it('keeps the queue per session across a remount and sends it when that session is ready', async () => {
    const send = vi.fn().mockResolvedValue()
    const first = composer({ send })
    await queue(first, 'for s1')
    await first.setProps({ sessionId: 's2' })
    expect(first.findAll('[data-queued-message]')).toHaveLength(0)
    first.unmount()
    wrappers.splice(wrappers.indexOf(first), 1)

    composer({ sessionId: 's1', enabled: true, queueable: false, send })
    await flushPromises()
    expect(send).toHaveBeenCalledWith('for s1')
  })

  it('stays closed to input when the session neither runs nor starts', () => {
    const wrapper = composer({ queueable: false })
    expect(wrapper.get('textarea').element.disabled).toBe(true)
  })
})
