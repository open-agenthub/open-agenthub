# Structured OpenCode conversation

Decision for #462: read the pinned CLI's native SQLite session store from the Node session
transport, using the same read-only database access already used for usage. OpenCode remains
the authority for messages, tool calls and session identity. The CLI still runs in its PTY;
login, menus, input and the managed policy plugin keep their existing paths.

The transport exports completed native messages to the existing append-only JSONL conversation
path. Message IDs determine ordering, with native part IDs determining order within a message.
A page contains at most 100 messages. An incomplete assistant message stops export at that
position so later messages cannot overtake it. Text, tool input and tool output are bounded;
omissions are visible. The journal stores the native cursor with each append, allowing a
restarted transport to resume without duplicating the last committed message. Subagent sessions
are excluded. The selected top-level session is the one remembered by the managed plugin.

The existing live transcript route, stored transcript uploads, backend access checks and masking
serve both owners and shared viewers. No new database or server port is exposed to a browser.
Completed messages appear after their native completion; live token streaming remains in the PTY.

Rejected alternatives: parsing terminal output loses tool structure and changes with screen
redraws. Fetching the plugin client's latest 100 messages loses earlier history after a missed
callback, and full snapshots grow without bound. A separate CLI server would add lifecycle and
authentication responsibilities unnecessarily. Writing to the native database would introduce
another history authority. The normalized journal is disposable projection state, never a
replacement for the CLI's own database.

Verification must exercise the real pinned CLI/store, transport reconnect, more than one page,
tool success and error records, and a shared read through the backend with existing masking.
