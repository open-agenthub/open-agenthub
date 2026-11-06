# Codex live Workspace proof (#452)

Reproduces "the Workspace stops while Codex keeps working; sent messages stay *waiting for the
agent to take it*" with the real pinned Codex TUI in the real `common/server.js`. Generated
output (logs, rollouts, screenshots) stays in `$RIG_HOME`; nothing of it belongs in Git.

Prerequisites, in a scratch `RIG_HOME` outside the repository:

- `npm i --prefix $RIG_HOME/clis @openai/codex@<CODEX_VERSION from agent-runtime/codex/Dockerfile>`
- a static `proot` at `$RIG_HOME/proot` (the managed `requirements.toml` must be `/etc/codex`)
- `npm ci --ignore-scripts` in `agent-runtime/session-agent`, then
  `R=$RIG_HOME WT=$PWD/agent-runtime tests/CodexLiveProof/build.sh` after every runtime change
  (it copies node-pty from `/opt/session-agent`, so run it in a session pod)

## Rig: the pod's transcript route under a real Codex turn

`rig.mjs` runs Codex against a mock Responses model that plays a scripted turn, sends composer
messages (bracketed paste + Enter) while it runs, and follows `GET /agenthub/transcript` the
way `LiveTranscriptReader` does, once a second.

The stand-in explicitly allows `/internal/sessions/s1/agent-policy` requests so the real
managed PreToolUse hook can execute the scripted tools. Since #457, an empty `{}` policy
response fails closed: the image scenario never opens its image and silently exercises only
a small transcript. Keep the managed hook enabled rather than bypassing it; this proof tests
transcript following, while `tests/CodexInteractivePermissionsProof` covers policy decisions.

```sh
RIG_HOME=/abs/scratch SCENARIO=image AFTER=8000 node tests/CodexLiveProof/rig.mjs
```

| `SCENARIO` | turn |
|---|---|
| *(default)* | 200,000 lines of output, a background terminal, several tool calls |
| `image` | `view_image` of a 12 MB PNG (generated), then tool calls |
| `queue` | messages sent while one long tool call blocks |
| `sub` | a sub-agent (`multi_agent_v1.spawn_agent`) |
| `texts` | messages with outer/inner whitespace, tabs, 3 KB, CRLF, unicode; prints what Codex recorded |

`COMPACT=2000 TOKENS=5000` makes Codex compact during the turn. Look for `POLL STUCK` and the
`RESULT` lines in `$RIG_HOME/out/rig.log`.

## Browser: the Workspace end to end

`start.sh` serves the rig (`SERVE`), the proof host on :8090 — the real
`LiveTranscriptReader` + `SessionTranscripts.PageAsync` + `ConversationPage`, with the stored copy
being what the session agent uploaded — the stand-in hub on :8080 (`hub.mjs`: canned answers for
the rest, the terminal socket proxied to the session agent, `POST /__drop` destroys the socket)
and Vite on :5173. `browser.mjs` then drives Chromium (Playwright's headless shell): it sends a
task and messages from the composer, drops the agent socket, sends one more, and logs the
conversation rows and pending rows each second. `TASK`/`MSG_SUFFIX` choose what is sent; screenshots
go to `$RIG_HOME/shots`.

```sh
RIG_HOME=/abs/scratch SCENARIO=image tests/CodexLiveProof/start.sh
RIG_HOME=/abs/scratch TAG=fix SCENARIO=drop node tests/CodexLiveProof/browser.mjs
```

Measured on main (Codex 0.166.0): a 13 MB rollout line stopped the cursor at the byte before it
for the rest of the session; `texts` showed `"a\n"`, `"a   "` and `"   a"` recorded as `"a"`.
