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

Prompt hooks mean working; a successful Stop means idle unless it continues with a fleet message.
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
It expires after 1.5 seconds of quiet and is identified by a tooltip. QuestionPending, pending
approval records and detected CLI menus override that approximation with a waiting label.
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
