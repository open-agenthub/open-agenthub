# Issue 336: context-clear handover, main vs branch

Pinned Claude Code 2.1.287 and Codex 0.160.0 ran in the PTY of the real `common/server.js`
under static proot, with the shipped hooks and requirements.toml. The work directory was a
git repository. Fixtures: a recording mock model, a stand-in hub (policy allow, empty inbox),
and the clear request POSTed to the pod's `/agenthub/context-clear`, which is what the backend
forwards the `context_clear` tool call to. `fault-server.cjs` wraps the PTY: `drop` loses the
first input after the clear, `busy` writes invisible output for 20 s after it, and `restart`
kills the CLI and the session agent 1 s after it. A second process then resumes on the same
home with `AGENTHUB_RESUME=1`.

"Delivered" means a model request contained the handover. "First user turn" is read from the
newest native transcript (Claude `projects/*.jsonl`, Codex `sessions/**/rollout-*.jsonl`).
"Fresh" means that request did not contain the old task's marker.

## main (89c1d2c)

| agent | fault | delivered | first user turn of the new transcript | model requests |
|---|---|---|---|---|
| Claude | none | yes, 8.2 s | handover | old → handover |
| Claude | drop | **no** | none (no new turn) | old |
| Claude | busy | **no** | none | old |
| Claude | restart | **no** | none | old |
| Codex (shipped requirements) | none | **no** | none: `/new` opened the worktree menu | old |
| Codex (shipped requirements) | owner answers the menu, then writes | **no** | the owner's message | old → owner-next |
| Codex (`worktrees = false`) | none | yes, 7.2 s | handover | old → handover |
| Codex (`worktrees = false`) | drop / busy / restart | **no** each | none | old |
| Claude / Codex | queued owner message + skill-reminder continuation | yes | handover | old → queued/reminder (old conversation) → handover |

On main, the only trace of the failure was one `[agent] Context clear failed` line in the
terminal (and not even that for `drop`). Nothing was logged.

## branch (final code)

| agent | fault | delivered | seconds after the request | first user turn | fresh |
|---|---|---|---|---|---|
| Claude | none | yes | 8.4 | handover | yes |
| Claude | drop | yes, 2nd attempt | 19.7 | handover | yes |
| Claude | busy | yes | 26.5 | handover | yes |
| Claude | restart | yes, by the resumed process | 16.7 | handover | yes |
| Claude | queued + reminder | yes | 7.7 | handover | yes |
| Codex | none | yes | 7.1 | handover | yes |
| Codex | drop | yes, 2nd attempt | 18.5 | handover | yes |
| Codex | busy | yes | 26.5 | handover | yes |
| Codex | restart | yes, by the resumed process | 17.7 | handover | yes |
| Codex | queued + reminder | yes | 7.7 | handover | yes |
| Codex, menu forced back on | owner answers the menu at 70 s, then writes | yes, at the next safe point | 76.4 | owner's message, then the handover | yes |

With the menu forced back on (the branch runtime, `worktrees = false` removed), the handover
was not typed into the menu. The Workspace got
`{"subtype":"handover","failed":true,"reason":"context_clear_input_not_ready","next":"it is sent at the next safe point"}`,
the agent log got `Context handover not delivered (...)`, and after the owner's turn the
fallback delivered it and sent `failed:false`.

Each delivered handover appeared exactly once in the model requests.

## Not verified

- The backend forwarding and the in-pod MCP tool were not in this chain; #188/#251 proved
  them, and this change only adds the `handover` note to the forwarded outcome (unit test).
- A real pod restart restores the handover file from the state archive. Here the second
  process reused the home directory instead. The file is in the archived state directory,
  and its write forces the next archive tick.
- OpenCode was not run.
- The Workspace banner was checked through its parser test, not in a browser.
