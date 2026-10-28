# Workspace turn activity

A running session can be idle for hours. Deriving a spinner from its phase was rejected because
it would claim work while the agent waits at its prompt. The session agent already tracks turn
hooks for next-turn delivery, so Workspace reads that same coordinator's state.

Terminal viewers opt in with a subscribe/activity message on the existing WebSocket. The server
returns a current snapshot and sends changed activity frames to subscribed readers only. Older
viewers retain their raw PTY transport. Read-only agent terminals add activity=1 to the socket URL.
The hub sends the fixed subscription upstream on their behalf; it never pumps viewer input or
resize frames. Forwarding selected client control packets was rejected to keep the input boundary
unchanged. Shell sockets never receive this subscription. Activity is never written to scrollback or state archives;
a replay must not resurrect an old spinner. The short presentation timer exists only while a
viewer subscribes and is released with the last subscriber.

Each frame also carries `ready`: whether the CLI runs and takes input, independent of the turn
state, which stays `unknown` until the first hook. The Workspace's starting state ends on it
(`docs/session-starting.md`).

Prompt hooks mean working; a successful Stop means idle unless it continues with a fleet message.

**A question is not an approval (#252).** Waiting carries a reason: `approval` for a permission
dialog, `answer` for a question, and the Workspace says *Waiting for your approval* only for the
first or for a pending hub approval record. Claude Code 2.1.287 raises PermissionRequest to show
an AskUserQuestion screen, so the hook reports a PermissionRequest for that tool as a question.
Its PreToolUse hook also no longer files AskUserQuestion with the hub as an approval request; that made the
person grant "AskUserQuestion" before the question even appeared. A waiting frame without a
reason, from a runtime older than the frontend, reads as an answer: of the two labels, a wrong
"answer" asks for nothing that is not there, while a wrong "approval" did.

The waiting state also went stale: Escape in either dialog cancels the whole turn, and no hook
reports it (verified with the pinned CLI, for a Bash permission dialog and a question alike).
The driver's interruption key therefore clears a wait as it clears work. Earlier it was ignored
in a wait, on the assumption that Escape only declined the tool; the turn then showed waiting at
an idle prompt until the next one. Rejected: clearing the wait when the CLI menu leaves the
screen, because the notice and the runtime would then disagree whenever the drawer's terminal
lags, and the runtime is what knows a turn ended.
Permission hooks mean waiting. A terminal approval submission clears that waiting presentation;
the visible CLI menu and the hub's pending approvals/questions always take precedence in the UI.
A prompt hook clears the previous hub question via its authenticated turn-started callback before
returning to the CLI. Clearing only on socket attach was rejected: an open Workspace can submit
many replies without reconnecting, leaving the old question hiding new work.
OpenCode reports an interrupted or failed top-level session.idle as presentation-only idle. It
must not become a delivery safe point: an abort must not immediately start a queued peer turn.
The native Interrupt hook likewise clears presentation without changing the coordinator's
safe-point state, since Stop is not run for a keyboard interruption. The other hooked runtime's
pinned CLI has no interruption hook: its verified single-Escape cancellation clears presentation
only while busy and outside a permission wait. Its StopFailure hook covers model errors. These
paths never make a queued peer message eligible for delivery.
A failed model call can also skip Stop. For that path the driver reads the structured
task_complete.error marker from a bounded, cached tail of its existing native transcript. A
completion older than the latest prompt cannot suppress the new turn. Successful completion
markers do not override hooks, since a Stop may still be handing over a continuation.
A generic Escape rule was rejected: another runtime requires two presses, so interruption input
is only recognized by the driver whose pinned CLI was verified. Visible menus retain precedence.

For sessions without interactive turn hooks, recent PTY output is only an approximation of work.
It expires after 1.5 seconds of quiet and is identified by a tooltip. Pending approval records and
detected CLI menus override that approximation with a waiting label.

**"Waiting for your answer" means a question is open (#343).** The Workspace used to say it also
whenever the session's `questionPending` was set. The hub sets that flag on every "question" a
hook sends to `/notify`, because the chat relays open a session's thread on that event
(`docs/chat-relay.md`). For the relays it means the turn ended and the person may reply. Claude
Code sends it as its `idle_prompt` notification about 60 seconds after every answer, and the
Codex, Cursor and OpenCode turn hooks send it at the end of every turn. So an idle session said
*Waiting for your answer* until the next prompt, nearly all the time. The Workspace now says it
only when the runtime reports an open question tool or the screen shows a CLI menu. The runtime
learns of the tool from its own events: PermissionRequest, or for Codex PreToolUse, opens it.
PostToolUse, OpenCode's `question.replied` or `question.rejected`, the interruption key, the next
prompt or the turn's end closes it. The first Enter no longer closes a question, as it still
closes an approval. Claude Code steps through several questions and ticks a multi-select list
with Enter, and the question stays open until PostToolUse.
Rejected: no longer setting `questionPending` for idle notifications and turn ends. The relays,
the session list's *waiting on you* and Home's *needs you* rely on it meaning "your turn".
Streaming sessions use their existing streamed-turn busy state. Disconnected, stopped and unknown
states show no working spinner. Reduced-motion viewers keep a static marker instead of rotation.

The component sits below readable conversation history, next to the input area, and shares the
stopped notice's surface and colors. Waiting uses a static marker and an explicit answer/approval
label so it cannot be mistaken for a model still working.

Verification used the real session server and three pinned interactive CLIs with synthetic model
responses, including actual model HTTP failures and keyboard interruptions. Real Chromium at
1440 and 390 pixels exercised working, persisted questions/approvals, native menus, reconnection
and reduced motion against the real backend/store. Viewer links use the real access resolver and
terminal proxy; network tests additionally assert legacy and shell isolation. The no-hook
approximation was exercised by disabling the capability on a real PTY, rather than claiming a
native-hook guarantee for runtimes without hooks. This is local runtime proof, not a deployment.
