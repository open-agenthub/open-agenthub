# Session MCP restriction decisions

Server restrictions apply to the configuration assembled on each session start or resume.
The assembler removes matching inline, catalog, built-in skill-library and session API
entries before constructing gateway entries. The runtime startup policy also disables
injection of agenthub_files, agenthub_browser, agenthub_network and agenthub_sessions.
Blocking a built-in removes its tools; the session can continue using its other tools.

A changed server list requires restarting the session. Rewriting mounted configuration
alone was rejected because the runtimes load their server lists at different times; it
would make the effective configuration depend on which runtime was selected. Restarting
also rebuilds gateway credentials. The share dialog states this requirement before saving.
Previously returned results remain part of the conversation.

Supported policy hooks read tool restrictions on each call. Matching follows the selected
runtime's tool namespace; names that map to the same runtime identifier share the deny.
Resource operations need server attribution when a server restriction exists. Unscoped
resource discovery is denied in that case because it can query multiple servers.

OpenClaw has no per-call policy hook. Cursor tool restrictions remain unavailable until
its MCP hook can be verified with the real CLI without an account. Both retain server
restrictions through startup configuration. The dialog preserves saved tool restrictions
when changing agents but disables editing them for these runtimes. Silently presenting
unsupported tool restrictions as active was rejected.
