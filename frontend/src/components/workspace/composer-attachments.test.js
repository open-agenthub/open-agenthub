// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const mocks = vi.hoisted(() => ({ api: {} }))
vi.mock('../../api.js', () => ({ api: mocks.api }))

import WorkspaceComposer from './WorkspaceComposer.vue'
import { workspaceDrafts, workspaceQueues } from '../../lib/session-drafts.js'

// #148: images pasted or attached in the terminal-transport composer reach the CLI as pasted paths.
const wrappers = []
let nextId = 0
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  workspaceDrafts.clear()
  workspaceQueues.clear()
})

function uploads() {
  Object.assign(mocks.api, {
    reserveSessionFile: vi.fn(async (_id, data) => ({ file: { id: String(++nextId).padStart(32, '0') }, upload: { kind: 'proxy', url: '/u', name: data.name } })),
    uploadSessionFile: vi.fn().mockResolvedValue(null),
    completeSessionFile: vi.fn(async (_id, fileId) => ({ id: fileId })),
    deleteSessionFile: vi.fn().mockResolvedValue(null)
  })
}

function composer(props) {
  uploads()
  const wrapper = mount(WorkspaceComposer, { props: { sessionId: 's1', enabled: true, queueable: false,
    send: vi.fn().mockResolvedValue(), attach: vi.fn(async ids => ids.map(id => `/workspace/.agenthub/files/${id}/x.png`)), ...props } })
  wrappers.push(wrapper)
  return wrapper
}

function image(name = 'image.png') {
  return new File([new Uint8Array([137, 80, 78, 71])], name, { type: 'image/png', lastModified: 1 })
}

async function paste(wrapper, { files = [], text = '' }) {
  const event = new Event('paste', { bubbles: true, cancelable: true })
  Object.defineProperty(event, 'clipboardData', { value: { files, getData: type => type === 'text/plain' ? text : '' } })
  wrapper.get('textarea').element.dispatchEvent(event)
  await flushPromises()
  return event
}

describe('composer attachments on the terminal transport', () => {
  it('a pasted screenshot is uploaded under a pasteable name and sent as a path ahead of the text', async () => {
    const wrapper = composer()
    const event = await paste(wrapper, { files: [image()] })
    expect(event.defaultPrevented).toBe(true)
    const name = mocks.api.reserveSessionFile.mock.calls[0][1].name
    expect(name).toMatch(/^pasted-image-\d{8}-\d{6}\.png$/)
    expect(wrapper.findAll('[data-attachment]')).toHaveLength(1)

    await wrapper.get('textarea').setValue('What is wrong here?')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const id = mocks.api.completeSessionFile.mock.calls[0][1]
    expect(wrapper.props('attach')).toHaveBeenCalledWith([id])
    expect(wrapper.props('send')).toHaveBeenCalledWith('What is wrong here?', { paths: [`/workspace/.agenthub/files/${id}/x.png`] })
    expect(wrapper.findAll('[data-attachment]')).toHaveLength(0)
    expect(wrapper.get('textarea').element.value).toBe('')
  })

  it('an image alone can be sent', async () => {
    const wrapper = composer()
    await paste(wrapper, { files: [image()] })
    expect(wrapper.get('button[type=submit]').element.disabled).toBe(false)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.props('send')).toHaveBeenCalledWith('', { paths: [expect.stringMatching(/^\/workspace\//)] })
  })

  it('text in the same paste wins over its picture', async () => {
    const wrapper = composer()
    const event = await paste(wrapper, { files: [image()], text: 'A1\tB1' })
    expect(event.defaultPrevented).toBe(false)
    expect(mocks.api.reserveSessionFile).not.toHaveBeenCalled()
  })

  it('a refused hand-over keeps the draft and the attachment, and says why', async () => {
    const wrapper = composer({ attach: vi.fn().mockRejectedValue(new Error('The agent is not running, so it cannot take attachments now.')) })
    await paste(wrapper, { files: [image()] })
    await wrapper.get('textarea').setValue('Look')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.props('send')).not.toHaveBeenCalled()
    expect(wrapper.get('[role=alert]').text()).toContain('not running')
    expect(wrapper.get('textarea').element.value).toBe('Look')
    expect(wrapper.findAll('[data-attachment]')).toHaveLength(1)
  })

  it('a message queued while the session starts keeps its files and hands them over once ready', async () => {
    const wrapper = composer({ enabled: false, queueable: true })
    await paste(wrapper, { files: [image()] })
    await wrapper.get('textarea').setValue('queued with an image')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[data-queued-message]').text()).toContain('pasted-image-')
    expect(wrapper.findAll('[data-attachment]')).toHaveLength(0)
    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(wrapper.props('send')).toHaveBeenCalledWith('queued with an image', { paths: [expect.stringMatching(/x\.png$/)] })
  })

  it('a queued message whose files cannot be handed over returns to the draft with them', async () => {
    const wrapper = composer({ enabled: false, queueable: true, attach: vi.fn().mockRejectedValue(new Error('nope')) })
    await paste(wrapper, { files: [image()] })
    await wrapper.get('textarea').setValue('later')
    await wrapper.get('form').trigger('submit')
    await wrapper.setProps({ enabled: true, queueable: false })
    await flushPromises()
    expect(wrapper.props('send')).not.toHaveBeenCalled()
    expect(wrapper.get('textarea').element.value).toBe('later')
    expect(wrapper.findAll('[data-attachment]')).toHaveLength(1)
  })

  it('without attach (a shared link) the composer offers no attachments', async () => {
    const wrapper = composer({ attach: undefined })
    expect(wrapper.find('[data-composer-attach]').exists()).toBe(false)
    const event = await paste(wrapper, { files: [image()] })
    expect(event.defaultPrevented).toBe(false)
    expect(mocks.api.reserveSessionFile).not.toHaveBeenCalled()
  })
})
