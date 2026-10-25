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
sends Ctrl+C to that same agent connection. Attachments go ahead of the text as pasted file paths,
the form every pinned CLI takes an image in; they have no delivery receipt either, so a failed
hand-over keeps the draft and its files (#148, `terminal-clipboard.md`).

### The agent CLI's own menus (#112)

Slash commands typed in the composer reach the CLI exactly as typed in the terminal: verified
with Claude Code 2.1.287, where the composer's paste-and-Enter of `/login` and `/model` opened
the same menus as typing them. What was missing is that the Workspace did not show those menus,
so a person working there did not see that the agent waited for a choice.

The view now reads the agent terminal's rendered screen — the xterm buffer the drawer would show,
kept current while the drawer is closed — with screen checks coalesced into 150 ms intervals
(`TerminalPane.vue`, `lib/cli-menu.js`). A
numbered list with the CLI's cursor in it, or above a dialog footer such as *Esc to cancel* or
*Press enter to confirm*, is a menu; a numbered list in the agent's reply has neither and is left
alone. A prompt for typed input inside such a dialog (*Paste code here if prompted >*) counts too.
When one appears, a notice shows *The agent is showing a menu* and the dialog's question; a
menu's options are buttons. A button sends the keys a person would press: arrows from the
highlighted option, then Enter, one key per message, and *Cancel* sends Esc. The notice follows
the rendered screen, including submenus, and disappears only when that screen leaves the menu.
The drawer stays closed until the viewer uses *Open terminal* in the notice or *Agent terminal*
in the toolbar. A drawer the viewer opened stays open when the menu disappears. This also
applies to typed-input prompts: the notice offers the terminal rather than opening it.
While a menu is open an opened drawer is taller: a CLI fits its dialog into the rows it has,
and Claude Code in the 14 rows of the normal drawer scrolled its login menu down to the one
highlighted entry. Such a cut list is marked as partial and its notice points at the terminal.

**Continuous redraws must not keep an old menu alive (#166).** The original trailing debounce
restarted on every PTY write. Codex 0.160.0 redraws its working spinner faster than the 150 ms
settling interval, so selecting a menu option and starting work could leave the old notice
visible although the live screen no longer contained a menu. Screen checks now have a bounded
wait: later writes join the pending check rather than postponing it. This is shared by every
agent; no provider-specific click handler or screen pattern is needed.

Rejected: clearing on a button click, because a choice can open a submenu or fail to reach the
CLI, and terminal keyboard input must dismiss the same notice. Also rejected: automatically
opening the drawer, because the buttons already answer choice menus and a resize can clip the
CLI's list. A bounded screen check preserves the screen as the source of truth during animation.

Reproduction used the pinned Codex in a real PTY, with its output rendered by xterm 5.5: open
`/model`, select a model and effort, then immediately start a turn against a local model endpoint
that keeps the response open. The original scheduler retained the old menu throughout six
seconds of spinner output; the bounded scheduler cleared it after 152 ms on the same output.
Chromium against the Workspace reproduced the stale notice with the original scheduler and
dismissed it during spinner output with the fix. The fixed browser flow was also verified
through the real session-agent server for both pinned CLIs. Claude Code 2.1.287 closed the
notice after selection and terminal Esc; the drawer stayed closed until explicitly opened,
and remained open after Esc when the viewer had opened it.

**Questions are shown whole, and "Type something" is a text field (#250).** Claude Code's
AskUserQuestion screen frames a long question in a bar (`│ …`) and wraps it to the terminal's
width. The detector took only the line right above the options, so the notice showed the last
line of the question, or the question's short header when it did not end in a question mark.
It now takes every line of the frame; a question drawn without one is joined with the lines it
wrapped from (each nearly as wide as the screen and not ending a sentence), so a heading or a
finished explanation above it stays out. The notice prints the question on its own, wrapped.

Every AskUserQuestion list ends in *Type something.*: on that row the CLI turns the label into a
text field, and Enter submits what was typed. Offered as a button it moved the cursor there and
pressed Enter on an empty field. The notice now shows a text field for it and sends arrows to
the row, the text as one bracketed paste and Enter. A paste rather than keys because Claude Code
2.1.287 keeps a pasted newline in the answer, and because a digit typed on the list would pick
that option. In a multi-select question the paste already ticks the row and Enter would untick
it, so there no Enter is sent. Rejected: sending the text through the composer, which the
composer refuses while a menu is open for exactly the reason that its Enter would pick the
highlighted option.

Verified with the pinned Claude Code in a real PTY against a mock model that asks an
AskUserQuestion, and in Chromium through the real session agent: the notice showed the full
three-line question, and the text typed into its field arrived in the CLI as the answer (the
tool result the model received), line break included.

**The other CLIs' questions and multi-select lists (#284).** Measured with the pinned CLIs, each
asking its own question tool:

- Codex 0.160.0 offers `request_user_input` in Plan mode only. In Default mode it answers the
  model "request_user_input is unavailable in Default mode". Its screen ends in *None of the
  above · Optionally, add details in notes (tab)*: the free text is a note, so the notice moves
  there, presses Tab, pastes and presses Enter. The model receives the option and `user_note: …`.
- OpenCode 1.18.35's `question` tool draws its dialog inside a `┃` frame, marks the highlighted
  row by colour only and ends in *↑↓ select  enter submit  esc dismiss*. None of that was
  recognised, so no notice appeared. The frame is now read past and the footer counts. Without a
  visible cursor the notice cannot know where the highlight is, so it answers with the option's
  digit, which OpenCode takes as picking it (or ticking it, or opening *Type your own answer*'s
  field). Arrows would have counted from a guessed row.
- Claude Code ends a multi-select list with an unnumbered *Submit* row (*Next* while more
  questions follow), on which the cursor can rest. The notice offers it as a button and counts it
  when moving. Before, the arrows to *4. Chat about this* stopped on that row instead. OpenCode
  moves on from a multi-select question with Tab and asks for Enter on a review of the answers,
  which the notice shows with *Submit answers*.

Neither CLI shows a question as an approval. Codex runs PreToolUse and no PermissionRequest for
`request_user_input`, and its policy hook files nothing in an interactive session. OpenCode's gate
leaves `question` out. Both were checked by logging the hook payloads of the real CLIs. The
Workspace shows these questions as *Waiting for your answer* because the notice is up.

Verified in Chromium through this repository's session agent with each real CLI: a long question
shown whole, a free-text answer with a line break, multi-select ticks with the step and review, all
arriving at the model as the tool's result. Against main's frontend the OpenCode question showed
no notice, and the Claude multi-select list had no way to submit.

While the CLI shows one of these screens, the composer refuses to send and keeps the draft:
its Enter would pick whatever option is highlighted, or submit the text as a login code. That
happened in testing: a `/login` sent into an open login menu chose the first option.

Verified end to end in Chromium against the real CLI, through this repository's session agent:
`/login` from the composer, the notice with the three login methods, the second option chosen by
button (the CLI opened its next step, which the notice followed), a composer message refused
while the menu was open, and Cancel closing the menu.

What is rendered natively and what falls back to the drawer, per agent:

| Agent | Permission requests | Other menus and prompts |
|---|---|---|
| Claude | Native approvals in the Workspace (hook) | Detected and offered as buttons: verified for `/login`, its sub-steps and `/model`; a step that wants typed input (the login code) offers the drawer |
| Codex | Native approvals (managed hook) | Verified with 0.160.0: model and effort submenus, selection, and dismissal during working-spinner output |
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

**Pending messages (#153, #186).** A message the composer sent is not yet a turn: a CLI that is
working keeps it in a queue of its own and writes it to its transcript only when it takes it. Verified
with the pinned CLIs in a PTY against a model held for 15 s: Claude Code 2.1.287 shows *Press up to
edit queued messages* and records a `queue-operation` until the turn ends, then the `user` entry;
Codex 0.160.0 shows *Messages to be submitted after next tool call* and writes the user item when it
submits it. Until then the message is shown below the conversation as *sent, waiting for the agent
to take it*, and leaves that list when the conversation holds one more user turn with its text than
it did when it was sent — so "continue" sent twice is two messages. A slash command is not followed
(Codex records none of them), and neither is anything in a terminal-history conversation (Cursor,
OpenClaw, OpenCode), where there is no user turn to find it in. Each entry has a ✕ that stops showing
it as pending, in case a CLI ever records a message differently from how it was sent; that only
changes this view.

Fleet messages to the session join the same list while the hub has not recorded them delivered
(`priority-messages.md`), labelled *not yet received by the agent*: the row cannot tell the inbox
from a message the pod accepted for the end of the running turn (#173), which the hub leaves unmarked
until it is handed over. Once delivered they leave the list and the ✉ badge. A fleet message
reaches the CLI as a user turn headed `[AgentHub message from agent "<title>" (<id>)]` — typed into the
terminal or submitted by the Claude fleet mod, which Claude Code 2.1.287 records wrapped in *The
agenthub-fleet plugin sent a message:* and a note to the model — and the conversation shows that turn
as *Message from agent “<title>”* with the body, not as the person's own words.

Rejected: a delivery receipt from each CLI. None of them reports when it takes a queued message
except by writing the transcript, which the Workspace already reads every second. Also rejected:
reading the CLI's queue off its screen. The two CLIs draw it differently, and a redraw says only
that the queue was there, not which entry left it.

Verified in Chromium against the real Claude Code and Codex in this repository's session agent, with
the conversation served by the backend's `NativeTranscript` parser from the CLI's own transcript: a
message sent during a held turn was listed as pending and moved into the conversation when the CLI
took it; a fleet message was pending while undelivered and appeared as a received fleet message once
delivered — typed into the terminal (both CLIs), through the real fleet mod (Claude), and read from
the inbox (Codex) — with no ✉ badge left behind. Against main's frontend the same run showed no
pending entries, a badge of 1 after delivery, and the fleet message as the person's turn.

**Send now (#261).** A waiting entry can be delivered at once instead of when the turn ends. What
that means differs by where the message waits:

- A fleet message is still a row in the hub. *Send now* pushes that row into the running agent as
  a priority message (`POST /api/sessions/{id}/messages/{messageId}/push`), and *Interrupt and send*
  stops the turn first, like a priority or interrupting `agent_send`. The row keeps its id, and the
  pod leaves the ids it typed out of its end-of-turn take, so the message cannot arrive twice.
  A row the agent has already taken is refused (409), so nothing is delivered a second time.
  *Send now* types the message into the running prompt, and the CLI then treats it like
  anything typed there: Codex 0.160.0 (with its managed hooks) queued it behind a text-only turn
  and sent it when the turn ended, while *Interrupt and send* reached the model 0.8 s after the
  push. Only the interrupt is immediate whatever the turn is doing. The same held a click away
  in Chromium, with the session agent under this repository's `/etc/codex` requirements (#284).
  *Interrupt and send* reached the model 0.76 s after the click, and both pushes were confirmed
  `pty`. Without the managed hooks the pod cannot see the typed message submitted and answers
  `not_submitted`; the message then stays in the list although the model has it.
- A composer message is already in the CLI's own queue, so it offers only *Interrupt and send*.
  Pushing its text as well would deliver it twice. The button sends the driver's interruption key
  (Escape; two for OpenCode), not the composer's Ctrl+C. Measured with the pinned CLIs and a model
  holding the turn: Claude Code 2.1.287 sent the queued message as the next request right after
  the interrupt, and Codex 0.160.0, whose queue reads *press esc to interrupt and send
  immediately*, showed *Model interrupted to submit steer instructions* and sent it. Without an
  interrupt both take it at the next tool call or at the turn's end, as before.

Rejected: Claude Code's own *ctrl+x ctrl+s to send now*, drawn under a queued message. Sent as
one write or with a pause between the keys, it delivered nothing while the model held the turn
(2.1.287). Also rejected: deleting the CLI's queue entry and sending the text as a priority
message. No CLI offers a way to remove one entry, and the screen does not say which entry is which.

**When a message was written (#147).** Each user and agent message in the conversation shows a
compact time — the time alone for today, the day for earlier this year, the year beyond that — with
the full date as its hover title. The time is the one the CLI wrote on that line of its own transcript
(Claude Code's `timestamp`, Codex's rollout `timestamp`), which the backend already read into each
entry's `at`; it is shown in the viewer's time zone and locale. A message that waited in the CLI's
queue therefore shows when the CLI took it, not when it was typed. Tool rows stay compact and carry
none. A terminal-history conversation (Cursor, OpenClaw, OpenCode) has no per-message times to show,
and neither does a saved chat-mode stream: its events carry none, and a replay arrives all at once,
so the time it reached the browser would be wrong. A received fleet message carries the time of the
turn it arrived as; a pending entry the time it was sent — by the composer, or by the peer as the hub
stored it — and the ✉ panel's tiles use the same format.

Rejected: stamping messages with the time the browser first saw them. That is the poll's time, not
the message's; after a reload every message of the session would carry the same minute.

Verified in Chromium against the real Claude Code and Codex in this repository's session agent, with
the conversation served by the backend's `NativeTranscript` parser: every message's `datetime` was
the timestamp of its line in the CLI's own JSONL, and the label and title showed it in the browser's
time zone.

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
