# Built-in session tools and the allow list (#504)

## What happened

Codex sessions had built-in session tools (`container_stop`, `agent_inbox`) refused with
"Blocked by the session policy." Two separate causes turned up.

**The reported sessions ran an old Codex runtime.** Their pod's `policy-hook.js` and
`requirements.toml` hashes and the Codex 0.166.0 binary match the releases 1.14.0 to 1.16.0
exactly, and nothing from 1.17.0 on. That hook calls the backend up to four times per MCP tool
call in the TUI, with a 3 s timeout, no retry, and every failure for an `mcp__` tool answered with
the session-policy text. `tests/CodexBuiltinToolsProof` runs that release's driver, hook,
requirements and CLI in the real TUI against today's backend: one `/agent-policy` answer delayed
4 s refuses `container_stop` while `container_run` ran — the report. The current runtime retries
policy reads (since #457) and runs the same calls under the same fault. The cure there is a
runtime from 1.17.0 or later; nothing in this change is needed for it.

**With auto-approve off, the allow list covered built-in tools too.** An unattended session whose
allow list named `container_run` could start a helper and was then refused `container_stop` for
it, and could not read its own inbox. Claude, in the same configuration, files an approval request
nobody answers, and gives up after the poll window.

## Decision

Built-in tools that only read the session's own state or clean up what the session itself started
need no allow-list entry (`BuiltInSessionTools`): `agent_inbox`, `agents_list`, `session_list`,
`session_get`, `session_wait`, `container_list`, `container_logs`, `container_stop`,
`secrets_list`, `account_status` and `port_list`. Codex and OpenCode get this from the policy
matcher, unattended Claude sessions from the approval endpoint. The owner's MCP sharing block is
checked first and still refuses them.

Tools that extend what the session can do stay under the allow list: `session_create`,
`container_run`, `secret_request`, `port_request`, `agent_send`, `session_delete`,
`session_convert`, `account_switch`, `context_clear` and the browser tools. The server names are
reserved for the runtime's own servers: a user MCP server cannot take the name `agenthub_sessions`.

The general allow list (`allowedTools`) also counts for `mcp__` entries now. Claude passes that list
to `--allowedTools`, where such an entry allows the tool; the matcher for Codex and OpenCode read
only `allowedMcpTools`, so the same session spec allowed the tool under Claude and refused it under
Codex.

## Alternatives not taken

- **Exempt every built-in tool.** `session_create` can start a child with auto-approve on, which
  would take a session past its own allow list; `container_run`, `secret_request` and
  `port_request` reach beyond the session as it was configured.
- **Answer "allow" from `/agent-policy` for interactive auto-approve sessions.** The current hook
  already reaches the same answer through `/permission`; it would save two round trips but not fix
  the old runtime, whose PermissionRequest path calls the backend regardless.
- **Retries in the backend.** The failures in the old runtime were callbacks that did not answer
  in time; only the hook can retry them, which it does since 1.17.0.
