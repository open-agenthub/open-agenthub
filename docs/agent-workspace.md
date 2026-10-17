# One session, two surfaces

The session's **Workspace** tab puts the conversation above a docked composer and an optional
agent terminal or shell. **Terminal** and **Shell** remain available for terminal sessions; the
saved conversation is read in the Workspace only (the Transcript tab was removed, see
`transcripts.md`). Switching
surfaces does not change the session's execution mode, start another provider, or replay a task.

## UI provenance

The compact activity rows in `frontend/src/components/workspace/WorkLog.vue` port the row
geometry and disclosure layout of T3 Code's `apps/web/src/components/chat/WorkLog.tsx` to Vue.
The workspace arrangement, docked composer, message actions, and terminal drawer adapt the
interaction patterns in `ChatView.tsx`, `MessagesTimeline.tsx`, `MessageCopyButton.tsx`, and
`ThreadTerminalDrawer.tsx` at upstream revision
`c77a7b7eebd6ea8a3a63bbcaaa7c1707fe65c86e`:
<https://github.com/pingdotgg/t3code/tree/c77a7b7eebd6ea8a3a63bbcaaa7c1707fe65c86e>.
The upstream MIT notice is distributed at `/licenses/t3code.txt` in every frontend build.

This is a Vue port of selected UI elements, not an embedded copy of the complete T3 application.
Embedding that application would also bring its React runtime, RPC contracts, account state,
provider ownership, and server assumptions. Keeping AgentHub's existing transport avoids a
second session authority and preserves the current authentication and sharing checks.

## Execution and history

Settings → Interface saves the preferred surface in this browser. Workspace is the default;
the preference controls session opening and the initial selection in New session. An explicit
creation choice applies to that opening without replacing the saved preference. This is viewer
state, not a setting shared with other people viewing the session.

The old standalone chat surface and its alternate layout are removed. `WorkspaceStream.vue`
renders streaming sessions only in Workspace.

**Every new session runs on the PTY (#114).** The web app used to create an interactive Claude
session with the Workspace preference as `uiMode: "chat"`: Claude headless with streamed JSON and
no terminal. That was the live Workspace, but it cost the CLI itself — `/login` answered "login
isn't available in this environment" (verified against Claude Code 2.1.287 in both modes: on the
PTY the same `/login`, sent through the agent socket as the composer sends it, opens the CLI's
login menu), and the Terminal tab and agent drawer did not exist. There is no provider login flow
in the hub, so the CLI's own has to stay reachable. New sessions, conversions and copies are now
always `terminal`; the preference only picks the first view, and Workspace and Terminal switch in
every interactive session. The live following chat mode gave comes from the pod's transcript
instead (#111, `transcripts.md`).

The rejected alternative was keeping chat mode and building a login flow and menu rendering into
the stream-json transport: the CLI does not offer its interactive commands there, so every one
would have had to be re-implemented and kept in step with each CLI release.

The API value `uiMode: "chat"` still exists. Saved chat sessions keep it, and keep starting
headless on resume — switching a running conversation's transport would strand its attachment
delivery receipts — so they gain no terminal; a copy of one does. An API or MCP caller that asks
for `chat` explicitly still gets it.

Claude Workspace sessions keep their streaming JSON connection, attachment delivery receipts,
and native interrupt command. Compact activity rows, copy/quote actions, and a docked composer
now form their only conversation surface. Existing streaming sessions open in Workspace even
when the saved preference is Terminal, because their process speaks JSON rather than a CLI TUI.

Terminal-mode sessions use the native conversation API introduced by the transcript work
(see `transcripts.md`). The workspace polls it every second while visible, and for a running
session the hub reads the transcript from the pod by byte offset, so a finished message shows
within one to two seconds of the terminal (#111). This follows the CLI's transcript file, which it
writes per message, not token streaming. Cursor, OpenClaw and OpenCode, and older sessions without native history,
show the cleaned terminal fallback; OpenCode keeps its conversation in its own database under
`~/.opencode`, not in a JSONL file the reader could serve (see `transcripts.md`). Finished conversations drain all available pages too.

The composer reuses the mounted agent terminal's WebSocket. Bracketed paste preserves a
multiline prompt, and Enter is sent after the existing CLI paste delay. The pending Enter is
cancelled on a disconnect or session change. A send completing means bytes were sent to the
socket; terminal protocols provide no provider receipt. If the connection changes after paste,
the draft remains and the error asks the person to check the terminal before retrying. Stop
sends Ctrl+C to that same agent connection. Terminal-mode attachments are not offered because
they do not have chat-mode delivery receipts.

### The agent CLI's own menus (#112)

Slash commands typed in the composer reach the CLI exactly as typed in the terminal: verified
with Claude Code 2.1.287, where the composer's paste-and-Enter of `/login` and `/model` opened
the same menus as typing them. What was missing is that the Workspace did not show those menus,
so a person working there did not see that the agent waited for a choice.

The view now reads the agent terminal's rendered screen — the xterm buffer the drawer would show,
kept current while the drawer is closed — a moment after output pauses (`lib/cli-menu.js`). A
numbered list with the CLI's cursor in it, or above a dialog footer such as *Esc to cancel* or
*Press enter to confirm*, is a menu; a numbered list in the agent's reply has neither and is left
alone. A prompt for typed input inside such a dialog (*Paste code here if prompted >*) counts too.
When one appears, the agent drawer opens with a notice, *The agent is showing a menu* and the
dialog's question; a menu's options are buttons. A button sends the keys a person would press:
arrows from the highlighted option, then Enter, one key per message, and *Cancel* sends Esc. When
the menu goes, a drawer the view opened for it closes again; one the person opened or took over
stays. While a menu is open the drawer is taller: a CLI fits its dialog into the rows it has, and
Claude Code in the 14 rows of the normal drawer scrolled its login menu down to the one
highlighted entry. Such a cut list is marked as partial and its notice points at the terminal.

While the CLI shows one of these screens, the composer refuses to send and keeps the draft:
its Enter would pick whatever option is highlighted, or submit the text as a login code. That
happened in testing: a `/login` sent into an open login menu chose the first option.

Verified end to end in Chromium against the real CLI, through this repository's session agent:
`/login` from the composer, the notice with the three login methods, the second option chosen by
button (the CLI opened its next step, which the notice followed), a composer message refused
while the menu was open, and Cancel closing menu and drawer.

What is rendered natively and what falls back to the drawer, per agent:

| Agent | Permission requests | Other menus and prompts |
|---|---|---|
| Claude | Native approvals in the Workspace (hook) | Detected and offered as buttons: verified for `/login`, its sub-steps and `/model`; a step that wants typed input (the login code) opens the drawer |
| Codex | Native approvals (managed hook) | Same screen detection; its selection lists are numbered with a cursor, but this was not run against the Codex CLI here |
| OpenCode | Native approvals (policy plugin) | Its TUI dialogs are not numbered lists and are not detected: the *Agent terminal* button, as before |
| Cursor | Configured at start (`--force`), no per-call question | Same screen detection, not verified against the CLI |
| OpenClaw | Asked in its own terminal only (no hook) | Same screen detection, not verified against the CLI |

Rejected: a structured signal from each CLI. None of them reports its own menus — Claude Code's
hooks cover permission and notification events, not `/login` or `/model` — so the only signal
common to all five is the screen. Also rejected: pattern-matching the raw byte stream in the
session agent. A TUI redraws in place, so the stream says that a menu was drawn but not whether it
is still there; the rendered screen says both, and the browser already has it.

**A message written while the session starts is queued (#115).** Send used to stay disabled until
the agent socket connected, while the person could already type. The composer now accepts a
message while the session is Pending (starting or resuming) or the socket is not connected yet,
shows it as *Queued · will be sent when the agent is ready* with Edit and Withdraw, and sends the
queue in order once the socket is connected — one message at a time, the next only after the
previous was submitted, because two pastes interleaved in the CLI's input box would arrive as one
turn. "Ready" is the connected socket and nothing later: verified against Claude Code 2.1.287,
where the composer's paste-and-Enter sent the instant the socket opened, before the CLI had drawn
anything, still became the first user turn — the PTY holds the bytes until the CLI reads them.
Waiting for a provider-specific "prompt drawn" signal would have meant parsing every CLI's screen
for a guarantee the PTY already gives.

Exactly once: a queued message leaves the queue only when its send resolved. A send that failed
before writing anything (the socket dropped) is kept and retried on the next connect; one that
failed after the paste may have reached the terminal goes back into the draft with the reason,
since a retry could not tell whether it would be the second copy. When the session stops taking
input without having started — it failed, or was stopped — the queue goes back into the draft.
The queue is kept per session in memory, like drafts, so switching sessions does not lose it.

Only connected, writable interactive sessions can use the terminal composer. Viewers get the
conversation and read-only agent terminal; the shell stays owner-only. Read-only markup is
still escaped before Markdown rendering. Tool content is text, never executable HTML.

## Session features around both surfaces

Everything main added to the session page while this branch was open — the provider-account
picker with its usage-limit and automatic-failover notices, the ✉ message panel with its badge
and priority/interrupt tiles and the "message this agent" form, the auto-delete expiry badge,
the conversion card for a finished autonomous run, Files with download, and the browser
companion with its clipboard — lives in the session bar, in the notices under it, or in
`SessionWorkspace`, all of which wrap both surfaces. Nothing of it is part of the Terminal
surface, so none of it had to be ported into the Workspace and it cannot fall out of step
between the two. The alternative, a second copy of those controls inside the Workspace layout,
would have given every later session feature two places to be wired up and two to forget.

Three adjustments came with the merge:

- **Continuing an autonomous run** offers *Open workspace* and *Open terminal* instead of main's
  *Open terminal* and *Open chat* (Claude only). The transport follows the New session rule
  (always `terminal` since #114) and the session opens on the surface that was picked; see
  `session-mode-conversion.md`.
- **A failed history load is an error with Retry**, not main's "[no saved transcript]". In the
  former Transcript tab the two looked the same; in the Workspace an empty conversation over a running
  agent reads as "nothing happened yet", and a 503 must not say that.
- **The session title keeps at least 240 px.** The bar now holds the account picker, Files,
  the tabs and the actions; with the title allowed to shrink to nothing, it was cut to its
  first letter on a phone and to "Works…" at 1440 px. The controls wrap onto a second line
  instead.

## Closing the browser and Files panes

Both panes close from an × on their own tab in the companion strip, and with Escape while focus
is in the pane's chrome. That strip is shown at every width, so desktop and phone get the same
control; the phone's Files/Browser/Agent switcher only switches. *Files* and *Browser* in the
session bar open them again.

Closing only hides the pane in this view. The alternatives were rejected for what they break:

- **Stopping the browser on close** would end the page the agent is driving in the middle of its
  task; the lease is the agent's, and other viewers of a shared session would lose it too.
- **Dismissing the presentation on close** is what *Dismiss for everyone* already does, for every
  viewer. Closing for oneself must not reach anyone else.
- **Escape inside the remote browser** is not taken. There the keyboard is forwarded to the page,
  and capturing Escape would make a dialog inside that page impossible to dismiss.
- **Keeping the choice per tab in `TerminalView`** would reopen the pane on every switch between
  Workspace and Terminal. `SessionWorkspace` wraps both surfaces, so the choice is kept there, for
  the session that is open. A browser that stops and is requested again is a new lease and shows
  again; a file the agent presents again opens Files again, as before.

## Limits of this integration

The actual T3 application cannot yet connect as an AgentHub client. That requires a separate
provider/ACP adapter and capability negotiation. The workspace does not claim T3's Git
checkpoints, rollback, provider switching, worktree orchestration, or inline Git review; files
and browser companions use the existing AgentHub components.

## Verification

Frontend component tests exercise native and fallback histories, completed-session paging,
viewer access, surface switching on one connection, send/interrupt, connection failures,
per-session drafts, IME input, quoting, search, and escaped output. Runtime tests include a
local server with real TCP WebSockets, a child-process provider fixture, reconnect/replay,
and native transcript upload to an HTTP callback. That fixture does not call a real model.
The backend suite covers native parsing, conversation paging, and owner/shared access.

An integrated Chromium pass also exercises the real frontend with isolated HTTP/WebSocket
session fixtures: history paging and retry, search, tool disclosure, quoting, multiline send,
interrupt, terminal/shell drawers, view and session draft retention, and mobile navigation.
Desktop, tablet, and phone layouts were checked, including a 390 × 667 viewport. This caught
and corrected unbounded mobile transcript height and drafts lost on keyed view remounts.
Draft text remains in memory across session navigation; reload/sign-out clears it.

After the merge with main the same pass ran in the integrated browser with the fixture extended
by main's features, all driven from the Workspace view: switching the provider account
(confirmation, `PATCH …/credential`, notice) with the composer still in place; the ✉ badge
counting priority, interrupt and plain messages, and a priority+interrupt message sent from the
panel; a Files download (the name the app hands the browser is checked there, the bytes and the
non-ASCII filename in a local Chromium under a UTF-8 locale, because over CDP the remote browser
cannot save into this pod); the expiry badge and version label; an OpenCode session's Workspace;
converting a finished autonomous Codex run into the Workspace; New session with five agents at
1440 and 390 px without horizontal overflow; and the session bar at 390 px.

This browser pass uses test responses, not a real model. A live provider/pod deployment remains
a release check: the session container denies namespace creation and mounts, has read-only
cgroups, and exposes no container runtime socket, so it cannot host a nested Kubernetes worker.
