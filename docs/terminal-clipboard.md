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

## Images reach the CLI as a pasted path (#148)

Every pinned CLI attaches an image when its path is pasted into the input box on its own, the way a
file dragged into a desktop terminal arrives. That is the one form all five take, so it is the one
the terminal and the Workspace composer use:

1. The browser uploads the pasted or attached file to the session, as Files and the chat-mode
   composer do. A clipboard image always arrives as `image.png`; it is stored as
   `pasted-image-<date>-<time>.png`, and any other name is reduced to `[A-Za-z0-9._-]`.
2. `POST /api/sessions/{id}/terminal-attachments` (owner or collaborator: typing into the terminal
   and uploading) forwards the ids to the pod's `POST /agenthub/attachments`. The session agent
   materializes them with the same `AttachmentMaterializer` as a chat-mode attachment and answers
   with the path each one is pasted as.
3. The browser writes each path as a bracketed paste of its own on the agent terminal's connection,
   150 ms apart. From the terminal that is all; the person adds words and presses Enter. From the
   composer the message text follows as one more paste, then Enter.

What each CLI does with it, measured with the pinned versions in a PTY against a mock model that
records the request, and end to end in Chromium with a real PNG on the system clipboard pasted by
Ctrl+V into both the composer and the agent terminal, through this repository's session agent:

| Agent | Shown in its input | Reaches the model as |
|---|---|---|
| Claude Code 2.1.287 | `[Image #1]` | an `image` content block |
| Codex 0.160.0 | `[Image #1]` | `input_image` (data URL) inside `<image>` |
| OpenCode 1.18.35 | `[Image 1]` | `image_url` (data URL), for a model that declares image input |
| OpenClaw 2026.7.1-2 | the path as text | an image, loaded from the path when the turn is sent |
| Cursor 2026.10.16 | `[Image #n]` (its TUI code) | not verified: the CLI needs a real key to start |

Two of them need more than the path:

- **OpenClaw** loads an image named in a prompt only from its own directories (`media`,
  `workspace`, `canvas` under its state dir, and its tmp dir). A path under `/workspace` is
  detected and then dropped — its debug log says *Local media path is not under an allowed
  directory* — and the model gets the path as text. The driver's `terminalAttachmentPath` copies
  the file to `~/.openclaw/media/agenthub/<id>/` and that path is pasted; the copy is excluded from
  the state archive, the original stays in session storage.
- **OpenCode** refuses an image for a model it does not know to take images: the model got
  *Cannot read "…png" (this model does not support image input)* instead. Catalogue models declare
  it; a custom model endpoint (`custom-model-endpoints.md`) is declared without it, because the
  hub cannot know whether a self-hosted model reads images, so there the image does not arrive.

The shell keeps the old behaviour: an image pasted there is dropped with *Images and files cannot
be pasted into the terminal. Upload them in Files, where the agent can read them.* A shell has no
agent to read a path, and a shared link's viewer cannot upload. A paste carrying text and a picture
of it (a spreadsheet range, rich text) stays text, in the terminal and in the composer.

Rejected alternatives:

- **Pointing at Files, as before.** That was the decision this replaces. It made the person upload
  the image elsewhere and then tell the agent about it in words, while all five CLIs already know
  how to take an image when given its path.
- **The pod typing the path itself** (the session agent writing to the PTY after materializing).
  The browser's own text and Enter would then race the pod's writes on two connections into one
  input box; with the browser pasting, every byte goes out on the one terminal socket in order.
- **The chat-mode instruction text** (`[AgentHub attachments] … Inspect each attached image…`).
  The agent then has to decide to read the file with a tool, which costs a turn and depends on the
  model choosing to; a pasted path is attached by the CLI before the model sees the turn.
- **Sending the image bytes into the PTY** (an OSC or kitty graphics sequence). None of the CLIs
  reads an image from its input stream; each only reads a file or its own clipboard.

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

For #148, the same Chromium run with a real PNG on the system clipboard, against main's frontend,
showed the failure as reported: Ctrl+V into the Workspace composer did nothing, into the agent
terminal it showed the Files notice, and no model request carried an image. With this change, for
each of Claude Code, Codex, OpenCode and OpenClaw and for both the composer and the agent terminal,
the upload went through the pod's file store, the session agent materialized it, the CLI showed its
image marker, and the mock model behind the real CLI received the PNG as image content. The
backend proxy class was run against that live session agent as well (the path for a valid token,
401 for a wrong one).
