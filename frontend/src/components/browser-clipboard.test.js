// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import BrowserPane from './BrowserPane.vue'
import {
  copyBrowserClipboard, copySharedBrowserClipboard, pasteBrowserClipboard, pasteSharedBrowserClipboard
} from '../api.js'

vi.mock('../api.js', async importOriginal => ({
  ...(await importOriginal()),
  browserUrl: vi.fn().mockResolvedValue('ws://test/browser'),
  resizeBrowserViewport: vi.fn().mockResolvedValue(null),
  pasteBrowserClipboard: vi.fn().mockResolvedValue(null),
  copyBrowserClipboard: vi.fn().mockResolvedValue({ text: '' }),
  pasteSharedBrowserClipboard: vi.fn().mockResolvedValue(null),
  copySharedBrowserClipboard: vi.fn().mockResolvedValue({ text: '' })
}))

const novnc = vi.hoisted(() => ({ instances: [] }))
vi.mock('@novnc/novnc', () => ({
  default: class {
    constructor(host, url) {
      this.host = host
      this.url = url
      this.listeners = {}
      novnc.instances.push(this)
    }
    addEventListener(name, callback) { this.listeners[name] = callback }
    disconnect() { this.disconnected = true }
  }
}))

async function connectedPane(props) {
  const wrapper = mount(BrowserPane, {
    attachTo: document.body,
    props: { session: { id: 's1', browser: { phase: 'Running' } }, ...props }
  })
  await flushPromises()
  novnc.instances.at(-1).listeners.connect()
  await flushPromises()
  return wrapper
}
function shortcut(wrapper, key) {
  wrapper.get('.browser-canvas').element.dispatchEvent(
    new KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true, cancelable: true }))
}
function paste(wrapper, text) {
  const event = new Event('paste', { bubbles: true, cancelable: true })
  event.clipboardData = { getData: () => text }
  wrapper.get('.browser-canvas').element.dispatchEvent(event)
}

describe('browser clipboard', () => {
  beforeEach(() => {
    novnc.instances.length = 0
    vi.clearAllMocks()
  })

  it('pastes and copies through the session routes for a viewer with control', async () => {
    const wrapper = await connectedPane({ canWrite: true })

    paste(wrapper, 'local')
    shortcut(wrapper, 'x')
    await flushPromises()

    expect(pasteBrowserClipboard).toHaveBeenCalledWith('s1', 'local')
    expect(copyBrowserClipboard).toHaveBeenCalledWith('s1', true)
    wrapper.unmount()
  })

  it('uses the share-link routes when the pane is opened through a link', async () => {
    const wrapper = await connectedPane({ canWrite: true, sharedToken: 'link' })

    paste(wrapper, 'local')
    shortcut(wrapper, 'c')
    await flushPromises()

    expect(pasteSharedBrowserClipboard).toHaveBeenCalledWith('link', 'local')
    expect(copySharedBrowserClipboard).toHaveBeenCalledWith('link', false)
    expect(pasteBrowserClipboard).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('never touches the clipboard for a view-only viewer', async () => {
    const wrapper = await connectedPane({ canWrite: false })

    paste(wrapper, 'local')
    shortcut(wrapper, 'c')
    await flushPromises()

    expect(pasteBrowserClipboard).not.toHaveBeenCalled()
    expect(copyBrowserClipboard).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('stays out of the way until the browser picture is live', async () => {
    const wrapper = mount(BrowserPane, {
      attachTo: document.body,
      props: { session: { id: 's1', browser: { phase: 'Running' } }, canWrite: true }
    })
    await flushPromises()

    paste(wrapper, 'early')
    await flushPromises()

    expect(pasteBrowserClipboard).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows a failed copy in the status strip', async () => {
    copyBrowserClipboard.mockRejectedValueOnce(Object.assign(new Error('502'), { status: 502 }))
    const wrapper = await connectedPane({ canWrite: true })

    shortcut(wrapper, 'c')
    await flushPromises()

    expect(wrapper.get('[role="status"]').text()).toBe('Copy from the browser failed.')
    wrapper.unmount()
  })
})
