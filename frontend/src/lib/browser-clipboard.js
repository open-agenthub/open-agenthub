// The remote browser is a VNC picture: a shortcut pressed over it reaches Chromium as a key,
// and Chromium's clipboard is the one inside its pod. So Ctrl/Cmd+V pasted whatever had last
// been copied *in the pod*, and a copy there never reached the user's own clipboard. These
// shortcuts are therefore taken out of the VNC stream and carried out against the local
// clipboard, with the remote side done by the backend through the browser's own input.

// Must match the backend and the browser supervisor; checked here so an oversized paste is
// refused without a round trip.
export const MAX_CLIPBOARD_CHARS = 262_144

// Ctrl+Shift+C/X are left alone: in Chromium they open the inspector or mean something else to
// the page, and taking them would change what the remote browser does. Ctrl+Shift+V is the
// usual "paste as plain text", which is all this pastes anyway.
export function clipboardShortcut(event) {
  if (event.type !== 'keydown' || event.altKey || event.ctrlKey === event.metaKey) return null
  const key = event.key?.toLowerCase()
  if (key === 'v') return 'paste'
  if (event.shiftKey) return null
  if (key === 'c') return 'copy'
  if (key === 'x') return 'cut'
  return null
}

class EmptySelection extends Error {}

// Some browsers only let a page write the clipboard inside the user's gesture, and the selection
// arrives a round trip later. A ClipboardItem built from a promise is written within the
// gesture and filled in when the text arrives; where that is not supported, writeText after the
// fact is what remains. An empty selection leaves the local clipboard as it was.
async function writeWhenReady(textPromise, clipboard, ClipboardItemType) {
  if (!clipboard) {
    if (await textPromise.catch(() => '')) throw new Error('Clipboard API unavailable')
    return
  }
  if (typeof ClipboardItemType === 'function' && typeof clipboard.write === 'function') {
    const blob = textPromise.then(text => {
      if (!text) throw new EmptySelection()
      return new Blob([text], { type: 'text/plain' })
    })
    blob.catch(() => {})
    try {
      await clipboard.write([new ClipboardItemType({ 'text/plain': blob })])
      return
    } catch {
      // Falls through: a browser that rejects promise-valued items may still take writeText.
    }
  }
  const text = await textPromise.catch(() => '')
  if (!text) return
  if (typeof clipboard.writeText !== 'function') throw new Error('Clipboard API unavailable')
  await clipboard.writeText(text)
}

// paste(text) and copy(cut) talk to the backend; enabled() says whether this viewer may control
// the browser right now. notify(message) reports what the user would otherwise not notice.
export function attachBrowserClipboard(element, {
  paste,
  copy,
  enabled = () => true,
  notify = () => {},
  clipboard = globalThis.navigator?.clipboard,
  ClipboardItemType = globalThis.ClipboardItem,
  pasteEventTimeout = 150
} = {}) {
  let pendingPaste

  const sendPaste = text => {
    if (!text) return
    if (text.length > MAX_CLIPBOARD_CHARS) {
      notify('Clipboard text is too large to paste.')
      return
    }
    Promise.resolve()
      .then(() => paste(text))
      .catch(error => notify(error?.status === 413
        ? 'Clipboard text is too large to paste.'
        : 'Paste into the browser failed.'))
  }

  // The browser reads the clipboard into a paste event without asking for permission, so the
  // shortcut is left to fire one. Only where none arrives is the Clipboard API asked instead,
  // which may prompt.
  const readFallback = () => {
    pendingPaste = undefined
    if (typeof clipboard?.readText !== 'function') {
      notify('Clipboard access is not available in this browser.')
      return
    }
    Promise.resolve()
      .then(() => clipboard.readText())
      .then(sendPaste, () => notify('Clipboard access was denied.'))
  }

  const copyFromRemote = cut => {
    const text = Promise.resolve().then(() => copy(cut)).then(result => result?.text ?? '')
    text.catch(error => notify(error?.status === 413
      ? 'The selection is too large to copy.'
      : 'Copy from the browser failed.'))
    writeWhenReady(text, clipboard, ClipboardItemType)
      .catch(() => notify('Clipboard access was denied; the selection was not copied.'))
  }

  const onKeyDown = event => {
    const action = clipboardShortcut(event)
    if (!action || !enabled()) return
    // Stops noVNC on the canvas below from also sending the shortcut to the remote page.
    event.stopPropagation()
    if (action === 'paste') {
      clearTimeout(pendingPaste)
      pendingPaste = setTimeout(readFallback, pasteEventTimeout)
      return
    }
    event.preventDefault()
    copyFromRemote(action === 'cut')
  }

  const onPaste = event => {
    if (!enabled()) return
    event.preventDefault()
    event.stopPropagation()
    clearTimeout(pendingPaste)
    pendingPaste = undefined
    sendPaste(event.clipboardData?.getData('text/plain') ?? '')
  }

  element.addEventListener('keydown', onKeyDown, true)
  element.addEventListener('paste', onPaste, true)
  return {
    dispose() {
      clearTimeout(pendingPaste)
      element.removeEventListener('keydown', onKeyDown, true)
      element.removeEventListener('paste', onPaste, true)
    }
  }
}
