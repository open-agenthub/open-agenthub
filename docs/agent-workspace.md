# One session, two surfaces

The session's **Workspace** tab puts the conversation above a docked composer and an optional
agent terminal or shell. **Terminal**, **Shell**, and **Transcript** remain available for terminal
sessions. Switching
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
renders streaming sessions only in Workspace. The existing API value `uiMode: "chat"` remains
as a transport contract for interactive Claude sessions and for previously saved sessions;
other agents and automated sessions use terminal transport with either viewer surface. Keeping
that contract avoids stranding existing session history and attachment delivery receipts.

Claude Workspace sessions keep their streaming JSON connection, attachment delivery receipts,
and native interrupt command. Compact activity rows, copy/quote actions, and a docked composer
now form their only conversation surface. Existing streaming sessions open in Workspace even
when the saved preference is Terminal, because their process speaks JSON rather than a CLI TUI.

Terminal-mode sessions use the native conversation API introduced by the transcript work
(see `transcripts.md`). The workspace polls the saved conversation every four seconds while
visible; runtime uploads still happen about every thirty seconds. This is a saved conversation,
not token streaming. Cursor, OpenClaw and OpenCode, and older sessions without native history,
show the cleaned terminal fallback; OpenCode keeps its conversation in its own database under
`~/.opencode`, not in a JSONL file the reader could serve (see `transcripts.md`). Finished conversations drain all available pages too.

The composer reuses the mounted agent terminal's WebSocket. Bracketed paste preserves a
multiline prompt, and Enter is sent after the existing CLI paste delay. The pending Enter is
cancelled on a disconnect or session change. A send completing means bytes were sent to the
socket; terminal protocols provide no provider receipt. If the connection changes after paste,
the draft remains and the error asks the person to check the terminal before retrying. Stop
sends Ctrl+C to that same agent connection. Login and interactive CLI menus remain available
in the drawer. Terminal-mode attachments are not offered because they do not have chat-mode
delivery receipts.

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
  (`chat` for Claude in the Workspace, `terminal` otherwise) and the session opens on the
  surface that was picked; see `session-mode-conversion.md`.
- **A failed history load is an error with Retry**, not main's "[no saved transcript]". In the
  Transcript tab the two looked the same; in the Workspace an empty conversation over a running
  agent reads as "nothing happened yet", and a 503 must not say that.
- **The session title keeps at least 240 px.** The bar now holds the account picker, Files,
  four tabs and the actions; with the title allowed to shrink to nothing, it was cut to its
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
