// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Terminal } from '@xterm/xterm'
import { FILE_PASTE_NOTICE, attachTerminalClipboard, pasteHasFile } from './terminal-clipboard.js'

const writeTo = (term, data) => new Promise(resolve => term.write(data, resolve))

function clipboardData({ text = '', files = [] } = {}) {
  return {
    getData: type => (type === 'text/plain' ? text : ''),
    items: files.map(type => ({ kind: 'file', type }))
  }
}

function pasteEvent(data) {
  const event = new Event('paste', { bubbles: true, cancelable: true })
  Object.defineProperty(event, 'clipboardData', { value: data })
  return event
}

let term, host
afterEach(() => {
  term?.dispose()
  host?.remove()
})

// The real xterm, opened on a DOM node, so the assertion covers what it does with the key
// and the paste event rather than what a fake of it would do.
async function openTerminal(options = {}) {
  host = document.createElement('div')
  document.body.appendChild(host)
  term = new Terminal({ allowProposedApi: true })
  const notify = vi.fn()
  attachTerminalClipboard(term, { host, notify, ...options })
  term.open(host)
  const sent = []
  term.onData(data => sent.push(data))
  // What Claude Code and Codex switch on at start.
  await writeTo(term, '\x1b[?2004h')
  return { sent, notify, textarea: host.querySelector('textarea') }
}

function keydown(target, init) {
  const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init })
  target.dispatchEvent(event)
  return event
}

describe('paste shortcuts in the real xterm', () => {
  it.each([
    ['Ctrl+V', { key: 'v', code: 'KeyV', keyCode: 86, ctrlKey: true }],
    ['Ctrl+Shift+V', { key: 'V', code: 'KeyV', keyCode: 86, ctrlKey: true, shiftKey: true }],
    ['Cmd+V', { key: 'v', code: 'KeyV', keyCode: 86, metaKey: true }]
  ])('%s is left to the browser and never reaches the agent as ^V', async (_, init) => {
    const { sent, textarea } = await openTerminal()
    const event = keydown(textarea, init)
    expect(event.defaultPrevented).toBe(false)
    expect(sent).toEqual([])
  })

  it('without the handler, Ctrl+V is what used to reach the agent', async () => {
    host = document.createElement('div')
    document.body.appendChild(host)
    term = new Terminal()
    term.open(host)
    const sent = []
    term.onData(data => sent.push(data))
    keydown(host.querySelector('textarea'), { key: 'v', code: 'KeyV', keyCode: 86, ctrlKey: true })
    expect(sent).toEqual(['\x16'])
  })

  it('the browser paste that follows arrives as bracketed paste', async () => {
    const { sent, notify, textarea } = await openTerminal()
    textarea.dispatchEvent(pasteEvent(clipboardData({ text: 'line one\nline two' })))
    expect(sent).toEqual(['\x1b[200~line one\rline two\x1b[201~'])
    expect(notify).not.toHaveBeenCalled()
  })

  it('an image-only clipboard is not sent as an empty paste, and says where images go', async () => {
    const { sent, notify, textarea } = await openTerminal()
    const event = pasteEvent(clipboardData({ files: ['image/png'] }))
    textarea.dispatchEvent(event)
    expect(sent).toEqual([])
    expect(event.defaultPrevented).toBe(true)
    expect(notify).toHaveBeenCalledWith(FILE_PASTE_NOTICE)
  })

  // A headless browser can hand an image clipboard to the page as an empty paste.
  it('an empty paste sends nothing and stays quiet', async () => {
    const { sent, notify, textarea } = await openTerminal()
    textarea.dispatchEvent(pasteEvent(clipboardData()))
    expect(sent).toEqual([])
    expect(notify).not.toHaveBeenCalled()
  })

  it('a read-only terminal shows no notice', async () => {
    const { notify, textarea } = await openTerminal()
    term.options.disableStdin = true
    textarea.dispatchEvent(pasteEvent(clipboardData({ files: ['image/png'] })))
    expect(notify).not.toHaveBeenCalled()
  })

  it('Ctrl+C and other control keys still reach the agent', async () => {
    const { sent, textarea } = await openTerminal()
    keydown(textarea, { key: 'c', code: 'KeyC', keyCode: 67, ctrlKey: true })
    keydown(textarea, { key: 'x', code: 'KeyX', keyCode: 88, ctrlKey: true })
    expect(sent).toEqual(['\x03', '\x18'])
  })
})

describe('pasteHasFile', () => {
  it('sees images and other files, not text', () => {
    expect(pasteHasFile(clipboardData({ files: ['image/png'] }))).toBe(true)
    expect(pasteHasFile(clipboardData({ files: ['application/pdf'] }))).toBe(true)
    expect(pasteHasFile(clipboardData({ text: 'x' }))).toBe(false)
    expect(pasteHasFile(null)).toBe(false)
  })
})
