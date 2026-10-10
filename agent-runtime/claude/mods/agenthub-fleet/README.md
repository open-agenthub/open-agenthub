# agenthub-fleet

A [Claude Code mod](https://code.claude.com/docs/en/plugins/mods/overview) that delivers
Open AgentHub fleet messages into the Claude Code session it runs in. It ships inside the
Claude runtime image and loads in every Claude session there; nothing has to be installed.

What it does:

- Polls the session agent's loopback inbox (`GET http://127.0.0.1:7681/agenthub/mod/inbox`,
  bearer `AGENTHUB_MOD_TOKEN`) every 3 seconds; the poll doubles as the heartbeat that
  makes the session agent route messages here instead of typing them into the terminal.
- A **priority** message is submitted as the next prompt, headed
  `[AgentHub message from agent "<title>" (<id>)]`. With **interrupt**, the running turn is
  aborted first.
- A plain message sets a status line (`📨 1 fleet message — /inbox`), is printed by `/inbox`,
  and otherwise rides along as context with the next prompt the person sends.
- Without `AGENTHUB_MOD_TOKEN` in the environment the mod does nothing, so loading this
  directory into a developer's own session is harmless.

Why this is a mod and not a settings hook, what it deliberately does not do, and how the
pieces fit: `docs/claude-code-mods.md` and `docs/priority-messages.md` in the repository.

## Files

```
agenthub-fleet/
├── .claude-plugin/plugin.json   manifest
├── hooks/hooks.json             points at the hooks module
├── hooks/register.js            the hooks: session.start, turn.start/complete, prompt.submit, /inbox
├── hooks/lib.mjs                the logic without the mods API (also tested by the session-agent suite)
└── tests/agenthub-fleet.test.ts `claude plugin test`
```

## Checking it

```bash
claude plugin validate --strict agent-runtime/claude/mods/agenthub-fleet
claude plugin test agent-runtime/claude/mods/agenthub-fleet
```

Both run without a session, a login or the network, and the Claude image build runs them
against the pinned CLI (`CLAUDE_CODE_VERSION` in `agent-runtime/claude/Dockerfile`). Tested
with Claude Code **2.1.287**, the first version with mods in the terminal; `claude plugin test`
needs that version or later (2.1.285 has `validate` only).

To try it in a session of your own, start Claude Code with the directory loaded and a token
that matches a session agent listening on `127.0.0.1:7681`:

```bash
AGENTHUB_MOD_TOKEN=... claude --plugin-dir agent-runtime/claude/mods/agenthub-fleet
```

`/plugin` then shows `1 mod active · agenthub-fleet`.
