import { describe, expect, it, vi } from 'vitest'
import { Terminal } from '@xterm/xterm'
import { attachTerminalClipboard, decodeOsc52 } from './terminal-clipboard.js'

const b64 = text => btoa(String.fromCharCode(...new TextEncoder().encode(text)))
const flush = () => new Promise(resolve => setTimeout(resolve, 0))
const writeTo = (term, data) => new Promise(resolve => term.write(data, resolve))

describe('decodeOsc52', () => {
  it('decodes a UTF-8 payload for any selection target', () => {
    expect(decodeOsc52(`c;${b64('grüße 👋')}`)).toBe('grüße 👋')
    expect(decodeOsc52(`;${b64('x')}`)).toBe('x')
  })

  it('refuses clipboard reads and malformed payloads', () => {
    expect(decodeOsc52('c;?')).toBeNull()
    expect(decodeOsc52('c;')).toBeNull()
    expect(decodeOsc52('nosep')).toBeNull()
    expect(decodeOsc52('c;***')).toBeNull()
  })
})

describe('attachTerminalClipboard with the real xterm parser', () => {
  // The fake terminals in the component tests have no parser; this is the check that
  // xterm actually routes OSC 52 to the handler, which is what was missing.
  it('copies what the agent sends as OSC 52 (BEL and ST terminated)', async () => {
    const term = new Terminal({ allowProposedApi: true })
    const write = vi.fn()
    attachTerminalClipboard(term, { write })

    await writeTo(term, `\x1b]52;c;${b64('copied text')}\x07`)
    await writeTo(term, `\x1b]52;c;${b64('second')}\x1b\\`)
    await flush()

    expect(write.mock.calls).toEqual([['copied text'], ['second']])
    term.dispose()
  })

  it('ignores OSC 52 for shared viewers and after dispose', async () => {
    const write = vi.fn()
    const shared = new Terminal()
    attachTerminalClipboard(shared, { write, allowOsc52: false })
    await writeTo(shared, `\x1b]52;c;${b64('x')}\x07`)

    const own = new Terminal()
    attachTerminalClipboard(own, { write }).dispose()
    await writeTo(own, `\x1b]52;c;${b64('y')}\x07`)
    await flush()

    expect(write).not.toHaveBeenCalled()
    shared.dispose()
    own.dispose()
  })
})

describe('copy shortcuts', () => {
  function fakeTerm(selection) {
    const term = {
      hasSelection: () => !!selection,
      getSelection: () => selection,
      attachCustomKeyEventHandler: handler => { term.onKey = handler }
    }
    return term
  }
  const key = init => ({ type: 'keydown', key: 'c', preventDefault: vi.fn(), ...init })

  it('Ctrl+C copies an xterm selection instead of interrupting the agent', async () => {
    const write = vi.fn()
    const term = fakeTerm('selected')
    attachTerminalClipboard(term, { write })

    const event = key({ ctrlKey: true })
    expect(term.onKey(event)).toBe(false)
    expect(event.preventDefault).toHaveBeenCalled()
    await flush()
    expect(write).toHaveBeenCalledWith('selected')
  })

  it('Ctrl+C without a selection still reaches the agent as an interrupt', () => {
    const write = vi.fn()
    const term = fakeTerm('')
    attachTerminalClipboard(term, { write })
    expect(term.onKey(key({ ctrlKey: true }))).toBe(true)
    expect(write).not.toHaveBeenCalled()
  })

  it('Ctrl+Shift+C and Cmd+C copy, other keys pass through', async () => {
    const write = vi.fn()
    const term = fakeTerm('sel')
    attachTerminalClipboard(term, { write })
    expect(term.onKey(key({ ctrlKey: true, shiftKey: true, key: 'C' }))).toBe(false)
    expect(term.onKey(key({ metaKey: true }))).toBe(false)
    expect(term.onKey(key({ ctrlKey: true, key: 'v' }))).toBe(true)
    expect(term.onKey(key({ key: 'c' }))).toBe(true)
    await flush()
    expect(write.mock.calls).toEqual([['sel'], ['sel']])
  })

  it('swallows a rejected clipboard write', async () => {
    const term = fakeTerm('sel')
    attachTerminalClipboard(term, { write: () => Promise.reject(new Error('denied')) })
    term.onKey(key({ ctrlKey: true }))
    await flush()
  })
})
