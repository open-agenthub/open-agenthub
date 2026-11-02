# FleetPriorityProof (#390)

Proves that a priority fleet message reaches a **busy** Claude session. A child that works
through its task is in one long turn. Before #390 the fleet mod submitted the message as a
plugin prompt, which Claude Code runs only once the session is idle. It arrived after the
whole task, and the hub had already marked the row delivered, so `agent_inbox` was empty.

Generated output (`rig.log`, `req-*.json`) is not committed. Run the rigs in a scratch directory.

## `server-rig.mjs`: the real session agent

The given `agent-runtime` tree is mounted with proot as `/opt/session-agent`, with a private `/tmp`.
Its `session-agent/server.js` starts the real Claude Code through the real claude driver, with the
real agenthub-fleet mod from that tree. A stand-in hub treats each delivery answer as
`SessionMessageDelivery` does: `queued-for-mod`/`pty`/`chat` mark the row delivered, and anything
else leaves it for `agent_inbox`. A mock Messages API keeps the initial task busy with `STEPS`
Bash calls of `SLEEP_S` seconds each.

```bash
# proot: curl -fsSL -o $S/proot https://proot.gitlab.io/proot/bin/proot && chmod +x $S/proot
(cd agent-runtime/session-agent && npm ci --ignore-scripts)   # node-pty comes from /opt/session-agent
PROOT=$S/proot node tests/FleetPriorityProof/server-rig.mjs agent-runtime $S/after
PROOT=$S/proot MODE=autonomous node tests/FleetPriorityProof/server-rig.mjs agent-runtime $S/after-auto
PROOT=$S/proot CASE=lastanswer STEPS=1 SLEEP_S=4 node tests/FleetPriorityProof/server-rig.mjs agent-runtime $S/after-last
PROOT=$S/proot DENY_APPEND=1 node tests/FleetPriorityProof/server-rig.mjs agent-runtime $S/after-deny
# before: the same against a detached worktree of main (copy session-agent/node_modules into it)
PROOT=$S/proot node tests/FleetPriorityProof/server-rig.mjs $S/main-wt/agent-runtime $S/before
```

The `SUMMARY` line gives the pod's answer, when the model first saw the message (relative to the
send), when the turn ended, and what is left in the hub inbox. Results with Claude Code 2.1.316:

| case | main | branch |
|---|---|---|
| busy, interactive | `queued-for-mod` in 12 ms; seen 33.3 s after the send = after the turn | `queued-for-mod` after the ack (2.9 s); seen 6.2 s after the send, next request |
| busy, autonomous `-p` | seen 33.2 s, after the turn | seen 6.1 s |
| sent while the last answer streams | — | seen 6.3 s, at the turn's end; body once plus a pointer |
| append refused (`DENY_APPEND=1`) | — | `unavailable`/`mod_not_injected`; row stays in the inbox |

## `busy-rig.mjs`: the mod alone

No session agent: a stand-in mod inbox and the real CLI via `script(1)`, with `--plugin-dir`.
Use it to iterate on the mod quickly. Rewrite the port in a copy of the mod first:
`sed -i 's/127.0.0.1:7681/127.0.0.1:17681/' <copy>/hooks/register.js`.

Pitfalls: strip inherited `CLAUDE*`/`AGENTHUB*`/`ANTHROPIC*` variables (both rigs do), and do not
set `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, which turns the mod's `$.http.fetch` off.
