# Workspace turn activity

A running session can be idle for hours. Deriving a spinner from its phase was rejected because
it would claim work while the agent waits at its prompt. The session agent already tracks turn
hooks for next-turn delivery, so Workspace reads that same coordinator's state.

Terminal viewers opt in with a subscribe/activity message on the existing WebSocket. The server
returns a current snapshot and sends changed activity frames to subscribed readers only. Older
viewers retain their raw PTY transport. Activity is never written to scrollback or state archives;
a replay must not resurrect an old spinner. The short presentation timer exists only while a
viewer subscribes and is released with the last subscriber.

Prompt hooks mean working; a successful Stop means idle unless it continues with a fleet message.
Permission hooks mean waiting. A terminal approval submission clears that waiting presentation;
the visible CLI menu and the hub's pending approvals/questions always take precedence in the UI.
OpenCode reports an interrupted or failed top-level session.idle as presentation-only idle. It
must not become a delivery safe point: an abort must not immediately start a queued peer turn.
The native Interrupt hook likewise clears presentation without changing the coordinator's
safe-point state, since Stop is not run for a keyboard interruption. A failed model call also skips Stop. For that path the driver reads the structured
task_complete.error marker from a bounded, cached tail of its existing native transcript. A
completion older than the latest prompt cannot suppress the new turn. Successful completion
markers do not override hooks, since a Stop may still be handing over a continuation.
Inferring this from Escape
was rejected: another runtime may require two presses, or use Escape just to dismiss a menu.

For sessions without interactive turn hooks, recent PTY output is only an approximation of work.
It expires after 1.5 seconds of quiet and is identified by a tooltip. QuestionPending, pending
approval records and detected CLI menus override that approximation with a waiting label.
Streaming sessions use their existing streamed-turn busy state. Disconnected, stopped and unknown
states show no working spinner. Reduced-motion viewers keep a static marker instead of rotation.

The component sits below readable conversation history, next to the input area, and shares the
stopped notice's surface and colors. Waiting uses a static marker and an explicit answer/approval
label so it cannot be mistaken for a model still working.
