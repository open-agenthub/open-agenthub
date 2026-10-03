// Claude Code (and other TUIs) enable mouse tracking, select text themselves and hand the
// selection to the terminal as OSC 52 ("ESC ] 52 ; c ; <base64> BEL"). xterm.js ignores that
// sequence unless something registers for it, so the CLI reported a copy while the browser
// clipboard never changed — and because the app owns the mouse, the browser offers no
// selection of its own to copy instead.
export const OSC_CLIPBOARD = 52

// Returns the text an OSC 52 payload asks to copy, or null for anything else. A '?' payload
// is a request to *read* the clipboard; answering it would hand the browser clipboard to
// whatever runs in the session, so it is never honoured.
export function decodeOsc52(data) {
  const separator = data.indexOf(';')
  if (separator < 0) return null
  const payload = data.slice(separator + 1)
  if (!payload || payload === '?') return null
  try {
    const bytes = Uint8Array.from(atob(payload), c => c.charCodeAt(0))
    return new TextDecoder().decode(bytes)
  } catch {
    return null
  }
}

function defaultWrite(text) {
  return navigator.clipboard?.writeText(text)
}

// Ctrl+C copies only while xterm holds a selection (made with Shift+drag while the app
// tracks the mouse); without one it stays the interrupt the agent expects. Ctrl+Shift+C
// always copies, as in desktop terminals. Cmd+C covers macOS.
function isCopyShortcut(event, hasSelection) {
  if (event.type !== 'keydown' || event.key?.toLowerCase() !== 'c' || event.altKey) return false
  if (event.metaKey) return hasSelection
  if (!event.ctrlKey) return false
  return event.shiftKey || hasSelection
}

export function attachTerminalClipboard(term, { allowOsc52 = true, write = defaultWrite } = {}) {
  const copy = text => {
    if (!text) return
    Promise.resolve()
      .then(() => write(text))
      .catch(() => { /* clipboard unavailable (insecure context, no focus) — nothing to recover */ })
  }

  const osc = allowOsc52
    ? term.parser?.registerOscHandler?.(OSC_CLIPBOARD, data => {
      copy(decodeOsc52(data))
      return true
    })
    : undefined

  term.attachCustomKeyEventHandler?.(event => {
    if (!isCopyShortcut(event, term.hasSelection?.())) return true
    event.preventDefault?.()
    copy(term.getSelection?.())
    return false
  })

  return { dispose: () => osc?.dispose?.() }
}
