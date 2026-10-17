# Terminal clipboard

The agent terminal and the shell are xterm.js panes over the session's PTY. Both agent CLIs
run a full-screen TUI there, and the clipboard has to cross from the person's browser into a
pod that has none of its own. `frontend/src/lib/terminal-clipboard.js` owns both directions.

## Copy

The CLIs select text themselves and hand it over as OSC 52. The terminal registers for that
sequence and writes the browser clipboard; a request to *read* the clipboard through OSC 52 is
never answered, and a shared link's viewer gets no OSC 52 at all. Ctrl+C copies only while xterm
holds a selection and otherwise stays the interrupt.

## Paste: Ctrl+V is the browser's paste

Ctrl+V, Ctrl+Shift+V and Cmd+V are left to the browser. The key handler returns `false` without
cancelling the event, so xterm does not encode the key, the browser fires its native `paste` on
xterm's textarea, and xterm sends the text as bracketed paste — the path the context menu and
Shift+Insert always took. Claude Code and Codex enable bracketed paste and take the text as one
edit, multiline included.

What it replaced: xterm encoded Ctrl+V as `^V` (0x16). Both CLIs bind that key to "paste an
image from the system clipboard" and look for a clipboard inside the pod. Codex answers
`Failed to paste image: clipboard unavailable: … X11 server connection timed out because it
was unreachable`, Claude Code `No image found in clipboard`. The person's own clipboard was
never read.

Rejected alternatives:

- **`navigator.clipboard.readText()` on the shortcut.** Depending on the browser it asks for a
  clipboard permission or shows an extra "Paste" confirmation for every paste, and it does not
  exist outside a secure context (an HTTP pod-IP origin). The native paste event needs none of
  that, so there is no refused permission to handle at all.
- **Sending `^V` through and making the pod's clipboard work.** A clipboard inside the pod would
  be the pod's, not the person's; there is nothing in it to paste.

## Images and files go through Files

A paste without text — an image, a copied file — has nothing the PTY could carry. xterm would
send an empty bracketed paste, which both CLIs ignore silently. The terminal drops such a paste
and, when it held a file, says: *Images and files cannot be pasted into the terminal. Upload
them in Files, where the agent can read them.*

Files is named rather than the Workspace message box because its upload exists for every
agent, which reads the file through the provider-neutral `files` MCP. Workspace attachments
exist only on the streaming transport; a terminal-transport session has none
(`agent-workspace.md`), so pointing there would send a Codex user nowhere.

Uploading the image from the terminal and typing a path into the CLI was the alternative. It
does not fit how session files reach the agent: an attachment is delivered with a chat message
that tells the session agent which files belong to the turn, and a file in Files is fetched by
the agent through the MCP. The terminal has neither and no pod path to type, and inventing one
would give images a third delivery path with its own guarantees to keep.

## Why Codex mentions X11

The session pod sets no `DISPLAY` (only the browser runtime's own container has one). Codex's
clipboard library tries a display server regardless and reports the failed connection with the
words "timed out", but measured in a PTY with the pinned Codex it fails about 10 ms after the
key, so there is no wait to shorten, and nothing in the pod to change. With Ctrl+V no longer
reaching the CLI as `^V`, the terminal no longer triggers that lookup.

## Verification

`terminal-paste.test.js` drives the real xterm on a DOM node: without the handler Ctrl+V comes
out as `\x16`; with it the keydown is not cancelled, nothing is sent, and the following paste
event arrives as `\x1b[200~…\x1b[201~`. In real browsers against a fixture terminal socket that
turns bracketed paste on as the CLIs do, Ctrl+V and Ctrl+Shift+V delivered the clipboard text as
bracketed paste, a real PNG on the clipboard produced the notice and sent nothing, and the
previous code sent `\x16`. The pinned Claude Code and Codex in a PTY show the bracketed-paste
text in their input box.
