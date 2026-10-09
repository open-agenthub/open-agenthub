# One session, two surfaces

The session's **Workspace** tab puts the conversation above a docked composer and an optional
agent terminal or shell. **Agent**, **Shell**, and **Transcript** remain available. Switching
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

Claude chat-mode sessions keep their streaming JSON connection, attachment delivery receipts,
and native interrupt command. Their Workspace view adds compact activity rows, copy/quote
actions, and a docked composer without changing the runtime protocol.

Terminal-mode sessions use the native conversation API introduced by the transcript work
(see `transcripts.md`). The workspace polls the saved conversation every four seconds while
visible; runtime uploads still happen about every thirty seconds. This is a saved conversation,
not token streaming. Cursor and OpenClaw, and older sessions without native history, show the
cleaned terminal fallback. Finished conversations drain all available pages too.

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

A browser visual pass and a live provider/pod deployment remain release checks; neither is
established by these tests.
