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

// Ctrl+V used to reach the PTY as ^V (0x16). Claude Code and Codex bind that to "paste an
// image from the system clipboard" and look for one inside the pod, where there is none
// (Codex reports a display-server failure, Claude Code "No image found"); the person's own
// clipboard was never read. Returning false without preventDefault stops xterm from encoding
// the key, so the browser runs its native paste on xterm's textarea and xterm sends the text as
// bracketed paste — the path the context menu and Shift+Insert already took. A keypress some
// browsers fire for Ctrl+V is let through the same way: xterm cancels keypress events it
// handles, and cancelling that one would cancel the paste. navigator.clipboard.readText is
// deliberately not used: depending on the browser it asks for a permission or an extra
// "Paste" confirmation, and it does not exist outside a secure context; the native paste
// needs none of that, so there is no refused permission to handle.
function isPasteShortcut(event) {
  if (event.key?.toLowerCase() !== 'v' || event.altKey) return false
  return !!(event.ctrlKey || event.metaKey)
}

// A paste without text (an image, a copied file) has nothing the PTY could carry, and xterm
// would send an empty bracketed paste that both CLIs silently ignore. The paste is dropped,
// and when it held a file the person is pointed at Files: its upload works for every agent,
// which reads the file through the provider-neutral files MCP, whereas Workspace attachments
// exist only on the streaming transport. docs/terminal-clipboard.md has why the terminal
// does not upload the image itself.
function pasteText(clipboardData) {
  return clipboardData?.getData('text/plain') || ''
}

export function pasteHasFile(clipboardData) {
  return Array.from(clipboardData?.items || []).some(item => item.kind === 'file')
}

export const FILE_PASTE_NOTICE = 'Images and files cannot be pasted into the terminal. Upload them in Files, where the agent can read them.'

export function attachTerminalClipboard(term, { allowOsc52 = true, write = defaultWrite, host, notify = () => {} } = {}) {
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

  // Capture on the host runs before xterm's own listener on its textarea inside it.
  const onPaste = event => {
    if (term.options?.disableStdin || pasteText(event.clipboardData)) return
    event.preventDefault()
    event.stopPropagation()
    if (pasteHasFile(event.clipboardData)) notify(FILE_PASTE_NOTICE)
  }
  host?.addEventListener('paste', onPaste, true)

  term.attachCustomKeyEventHandler?.(event => {
    if (isPasteShortcut(event)) return false
    if (!isCopyShortcut(event, term.hasSelection?.())) return true
    event.preventDefault?.()
    copy(term.getSelection?.())
    return false
  })

  return {
    dispose: () => {
      osc?.dispose?.()
      host?.removeEventListener('paste', onPaste, true)
    }
  }
}
