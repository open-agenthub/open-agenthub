// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { attachBrowserClipboard, clipboardShortcut, MAX_CLIPBOARD_CHARS } from './browser-clipboard.js'

const flush = () => new Promise(resolve => setTimeout(resolve, 0))

function key(target, key, init = { ctrlKey: true }) {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init })
  target.dispatchEvent(event)
  return event
}
function pasteEvent(target, text) {
  const event = new Event('paste', { bubbles: true, cancelable: true })
  event.clipboardData = { getData: type => (type === 'text/plain' ? text : '') }
  target.dispatchEvent(event)
  return event
}

// A stand-in for ClipboardItem that records what was handed to clipboard.write.
class RecordingItem {
  constructor(items) { this.items = items }
}

describe('clipboardShortcut', () => {
  it('maps Ctrl and Cmd shortcuts to paste, copy and cut', () => {
    expect(clipboardShortcut({ type: 'keydown', key: 'v', ctrlKey: true })).toBe('paste')
    expect(clipboardShortcut({ type: 'keydown', key: 'V', metaKey: true, shiftKey: true })).toBe('paste')
    expect(clipboardShortcut({ type: 'keydown', key: 'c', metaKey: true })).toBe('copy')
    expect(clipboardShortcut({ type: 'keydown', key: 'x', ctrlKey: true })).toBe('cut')
  })

  it('leaves other keys, AltGr chords and the inspector shortcut to the remote browser', () => {
    expect(clipboardShortcut({ type: 'keydown', key: 'v' })).toBeNull()
    expect(clipboardShortcut({ type: 'keyup', key: 'v', ctrlKey: true })).toBeNull()
    expect(clipboardShortcut({ type: 'keydown', key: 'c', ctrlKey: true, altKey: true })).toBeNull()
    expect(clipboardShortcut({ type: 'keydown', key: 'c', ctrlKey: true, shiftKey: true })).toBeNull()
    expect(clipboardShortcut({ type: 'keydown', key: 'a', ctrlKey: true })).toBeNull()
  })
})

describe('attachBrowserClipboard', () => {
  let host
  let canvas
  let remoteKeys
  beforeEach(() => {
    host = document.createElement('div')
    canvas = document.createElement('canvas')
    host.appendChild(canvas)
    document.body.appendChild(host)
    // Stands in for noVNC, which listens on its canvas and forwards every key it sees.
    remoteKeys = []
    canvas.addEventListener('keydown', event => remoteKeys.push(event.key))
  })
  afterEach(() => host.remove())

  it('pastes the text a paste event carries and keeps the shortcut out of the VNC stream', async () => {
    const paste = vi.fn().mockResolvedValue(null)
    const clipboard = { readText: vi.fn() }
    attachBrowserClipboard(host, { paste, copy: vi.fn(), clipboard, pasteEventTimeout: 5 })

    const keydown = key(canvas, 'v')
    const event = pasteEvent(canvas, 'local text')
    await new Promise(resolve => setTimeout(resolve, 20))

    expect(remoteKeys).toEqual([])
    expect(keydown.defaultPrevented).toBe(false)
    expect(event.defaultPrevented).toBe(true)
    expect(paste).toHaveBeenCalledWith('local text')
    expect(clipboard.readText).not.toHaveBeenCalled()
  })

  it('reads the Clipboard API when the shortcut produced no paste event', async () => {
    const paste = vi.fn().mockResolvedValue(null)
    const clipboard = { readText: vi.fn().mockResolvedValue('from api') }
    attachBrowserClipboard(host, { paste, copy: vi.fn(), clipboard, pasteEventTimeout: 1 })

    key(canvas, 'v', { metaKey: true })
    await new Promise(resolve => setTimeout(resolve, 10))

    expect(paste).toHaveBeenCalledWith('from api')
  })

  it('says so when clipboard reading is denied or unavailable', async () => {
    const notify = vi.fn()
    const denied = attachBrowserClipboard(host, {
      paste: vi.fn(), copy: vi.fn(), notify, pasteEventTimeout: 1,
      clipboard: { readText: vi.fn().mockRejectedValue(new DOMException('denied', 'NotAllowedError')) }
    })
    key(canvas, 'v')
    await new Promise(resolve => setTimeout(resolve, 10))
    denied.dispose()
    attachBrowserClipboard(host, { paste: vi.fn(), copy: vi.fn(), notify, clipboard: null, pasteEventTimeout: 1 })
    key(canvas, 'v')
    await new Promise(resolve => setTimeout(resolve, 10))

    expect(notify.mock.calls).toEqual([
      ['Clipboard access was denied.'],
      ['Clipboard access is not available in this browser.']
    ])
  })

  it('refuses an oversized paste without sending it', async () => {
    const paste = vi.fn()
    const notify = vi.fn()
    attachBrowserClipboard(host, { paste, copy: vi.fn(), notify })

    pasteEvent(canvas, 'x'.repeat(MAX_CLIPBOARD_CHARS + 1))
    await flush()

    expect(paste).not.toHaveBeenCalled()
    expect(notify).toHaveBeenCalledWith('Clipboard text is too large to paste.')
  })

  it('copies the remote selection through a ClipboardItem written inside the gesture', async () => {
    const copy = vi.fn().mockResolvedValue({ text: 'remote selection' })
    const written = []
    const clipboard = {
      write: vi.fn(async items => { written.push(await items[0].items['text/plain']) }),
      writeText: vi.fn()
    }
    attachBrowserClipboard(host, { paste: vi.fn(), copy, clipboard, ClipboardItemType: RecordingItem })

    const event = key(canvas, 'c')
    expect(clipboard.write).toHaveBeenCalledTimes(1)
    await flush()
    await flush()

    expect(remoteKeys).toEqual([])
    expect(event.defaultPrevented).toBe(true)
    expect(copy).toHaveBeenCalledWith(false)
    expect(await written[0].text()).toBe('remote selection')
    expect(clipboard.writeText).not.toHaveBeenCalled()
  })

  it('falls back to writeText for a cut where ClipboardItem is unavailable', async () => {
    const copy = vi.fn().mockResolvedValue({ text: 'cut text' })
    const clipboard = { writeText: vi.fn().mockResolvedValue(undefined) }
    attachBrowserClipboard(host, { paste: vi.fn(), copy, clipboard, ClipboardItemType: null })

    key(canvas, 'x')
    await flush()
    await flush()

    expect(copy).toHaveBeenCalledWith(true)
    expect(clipboard.writeText).toHaveBeenCalledWith('cut text')
  })

  it('keeps the local clipboard unchanged when nothing is selected remotely', async () => {
    const notify = vi.fn()
    const clipboard = { write: vi.fn(async items => { await items[0].items['text/plain'] }), writeText: vi.fn() }
    attachBrowserClipboard(host, {
      paste: vi.fn(), copy: vi.fn().mockResolvedValue({ text: '' }), notify, clipboard, ClipboardItemType: RecordingItem
    })

    key(canvas, 'c')
    await flush()
    await flush()

    expect(clipboard.writeText).not.toHaveBeenCalled()
    expect(notify).not.toHaveBeenCalled()
  })

  it('reports a failed copy request and a denied clipboard write', async () => {
    const notify = vi.fn()
    const failing = attachBrowserClipboard(host, {
      paste: vi.fn(), copy: vi.fn().mockRejectedValue(Object.assign(new Error('502'), { status: 502 })),
      notify, clipboard: { writeText: vi.fn() }, ClipboardItemType: null
    })
    key(canvas, 'c')
    await flush()
    await flush()
    failing.dispose()

    attachBrowserClipboard(host, {
      paste: vi.fn(), copy: vi.fn().mockResolvedValue({ text: 'x' }), notify,
      clipboard: { writeText: vi.fn().mockRejectedValue(new DOMException('denied', 'NotAllowedError')) },
      ClipboardItemType: null
    })
    key(canvas, 'c')
    await flush()
    await flush()

    expect(notify.mock.calls).toEqual([
      ['Copy from the browser failed.'],
      ['Clipboard access was denied; the selection was not copied.']
    ])
  })

  it('passes every key through untouched while the viewer may not control the browser', async () => {
    const paste = vi.fn()
    const copy = vi.fn()
    attachBrowserClipboard(host, { paste, copy, enabled: () => false })

    key(canvas, 'c')
    key(canvas, 'v')
    const event = pasteEvent(canvas, 'ignored')
    await new Promise(resolve => setTimeout(resolve, 200))

    expect(remoteKeys).toEqual(['c', 'v'])
    expect(event.defaultPrevented).toBe(false)
    expect(paste).not.toHaveBeenCalled()
    expect(copy).not.toHaveBeenCalled()
  })

  it('stops intercepting after dispose', () => {
    const copy = vi.fn()
    attachBrowserClipboard(host, { paste: vi.fn(), copy }).dispose()
    key(canvas, 'c')
    expect(remoteKeys).toEqual(['c'])
    expect(copy).not.toHaveBeenCalled()
  })
})
