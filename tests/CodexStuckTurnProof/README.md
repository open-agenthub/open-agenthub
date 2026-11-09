# Codex stuck-turn proof (#562)

Drives the real pinned Codex TUI in the real `common/server.js` PTY against a scripted mock
Responses model. Nothing needs docker, root or a login. It shows which waits can keep a Codex
turn "Working" with no end, and whether Workspace messages reach the agent during that time.

## Setup

```bash
S=<scratch dir>; mkdir -p $S/rig
npm i --prefix $S/clis @openai/codex@<CODEX_VERSION from agent-runtime/codex/Dockerfile>
curl -fsSL -o $S/proot https://proot.gitlab.io/proot/bin/proot && chmod +x $S/proot
cp tests/CodexStuckTurnProof/{rig.mjs,build.sh,hang-mcp.cjs,proofs.sh} $S/rig/
(cd agent-runtime/session-agent && npm ci --ignore-scripts)
R=$S/rig WT=$PWD/agent-runtime $S/rig/build.sh     # rerun after every runtime change
```

`build.sh` lays out the branch runtime the way the image does, as `/opt/session-agent` and
`/etc/codex` inside proot. It replaces node-pty with the build from the pod's
`/opt/session-agent`, because node-pty cannot be compiled in a session pod. Every run gets a
private `/tmp`. Run one scenario at a time, because all runs share the fixture HOME `$S/rig/home`.

## Scenarios (`SCENARIO=… node rig.mjs`, output in `$S/rig/out-<scenario>` or `OUT=`)

| Scenario | What the model does |
|---|---|
| `bg` | starts `CMD` (default `sleep infinity`) through unified exec with a 1 s yield, then answers; then sends a normal, composer, priority and interrupt message |
| `idlebg` | starts `CMD` in the background, then sends a normal message to the idle agent |
| `cmds` | one prompt per `CMDS` entry (`;;`-separated): `FG:<cmd>`, `BG:<cmd>`, `BGT:<cmd>` (tty), `YIELD:<ms>:<cmd>`, `POLL:<ms>:<cmd>` (exec, then `write_stdin` with that yield), `MCP:<namespace>/<tool> {json args}` |
| `stall` | the stream stops sending after `response.created` |
| `stalltool` | the model writes a sentence and starts `STALL_CMD` (default `sleep 1`) through exec in the same response. The request that carries the tool output then stalls. `INTERRUPT_PUSH=1` interrupts with a priority push instead of the Workspace interrupt |
| `down` | the model endpoint refuses connections; `RESTORE=1` brings it back |
| `compact` | `/compact` |
| `active` | negative control. `ACTIVE_PROMPT` with `ACTIVE` runs a build and polls it with `write_stdin` every `POLL_YIELD_MS` (20 s) for `ACTIVE_MS`. With `STREAMTEXT` or `STREAMREASON` (needs `WS=1`), the model streams text or reasoning-summary deltas every `STREAM_EVERY_MS` for `STREAM_MS` |
| `serve` | starts the owner's stall and keeps the agent up on `AGENT_PORT` for the browser proof until `$OUT/stop` exists |

`NOTICE_MIN` and `INTERRUPT_MIN` set `AGENTHUB_STALLED_TURN_NOTICE_MINUTES` and
`AGENTHUB_STALLED_TURN_INTERRUPT_MINUTES`. `MODE=autonomous PROMPT=…` runs `codex exec` the way
an Autonomous session does. `proofs.sh` runs the detection proofs of #562 one after another:
the negative controls, the autonomous interrupt, an interactive session that is never
interrupted, and the reconnect reason.

## Browser

`hub.mjs` is a stand-in hub on :8080. It answers the web app's `/api` calls for one running Codex
session and proxies its terminal socket to the rig's agent. `browser.mjs` opens the session in
Chromium (Playwright headless shell, libraries as in the rootless-chromium setup), sends a message
from the composer and waits for the stall notice. It then screenshots desktop and 390 px widths,
clicks Interrupt and checks that the notice goes:

```bash
(cd frontend && npx vite --port 5173 --strictPort) &
AGENT_PORT=19778 NOTICE_MIN=1 WS=1 KEEPALIVE_MS=5000 STALL_CMD='sleep infinity' SCENARIO=serve OUT=$O node $S/rig/rig.mjs &
RT=$S/rig/rt AGENT_PORT=19778 node tests/CodexStuckTurnProof/hub.mjs &
RT=$S/rig/rt OUT=$O NOTICE_WAIT_MS=150000 node tests/CodexStuckTurnProof/browser.mjs; touch $O/stop
```


Stall options: `WS=1` uses the Responses WebSocket transport. `KEEPALIVE_MS` sends a `response.in_progress` event (or `KEEPALIVE_TYPE`) at that interval while the stream is stalled. `PING_MS` sends WebSocket pings. `IDLE_MS` sets `stream_idle_timeout_ms`; Codex's default is used without it. The owner's screen in #562 comes from:

```bash
WS=1 KEEPALIVE_MS=5000 STALL_CMD='sleep infinity' SCENARIO=stalltool WATCH_MS=120000 node rig.mjs
```

For an MCP server, pass `EXTRA_TOML` with literal `\n` between lines. For example, the
stand-in server whose `wait` tool never answers:

```bash
EXTRA_TOML="[mcp_servers.hang]\ncommand = \"node\"\nargs = [\"$S/rig/hang-mcp.cjs\"]" \
  SCENARIO=cmds CMDS='MCP:mcp__hang/wait' CMD_WAIT_MS=240000 node rig.mjs
```

The stand-in hub answers `/peer/<id>` with a `Running` interactive child. That makes the real
`agenthub_sessions` server's `session_wait` wait out its whole `timeoutMs`.

`rig.log` holds `RESULT`, `CMD`, `MODEL` and `ACTIVITY` lines. `ACTIVITY` is the state the
Workspace shows. `screen-*.txt` files are the TUI as `@xterm/headless` renders it. Do not commit
any output.
