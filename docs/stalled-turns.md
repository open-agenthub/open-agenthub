# Stalled turns

## The failure (#562)

A Codex session stayed in one turn for 17 hours. The TUI showed `Working (17h 16m 37s • esc to
interrupt) · 1 background terminal running`, and under it the owner's message under `Messages to
be submitted after next tool call`. That message was never delivered. The Workspace said
"Working…" the whole time, and so did every other surface.

The pinned Codex 0.167.0, driven in the real `common/server.js` against a mock model
(`tests/CodexStuckTurnProof`), showed which waits have no limit and which have one:

| Wait | Limit in 0.167.0 |
|---|---|
| A process started through exec (dev server, `sleep infinity`, a program reading stdin, a tty program) | none needed: exec returns after its yield (at most 30 s) and the process stays as a background terminal. The turn goes on and ends. |
| `write_stdin` polling such a process | 300 s per call (`background_terminal_max_timeout`) |
| An MCP tool call that never answers | 300 s (the default `tool_timeout_sec`) |
| A model stream that sends nothing | `stream_idle_timeout_ms`, over SSE and over WebSocket, WebSocket pings included |
| **A WebSocket model response the server keeps alive with events but never finishes** | **none**: every event resets the idle timeout |
| **A model endpoint that refuses connections** | **none**: `Reconnecting... waiting for network`, even with `request_max_retries = 0`. It recovers within seconds once the endpoint is back. |

The first unbounded case reproduces the owner's screen line for line: the agent's last
sentence, then `Working (…) · 1 background terminal running`, then the queued message. While
the turn hangs, every way in is blocked:

- A normal Workspace message waits for the Stop hook, which never runs.
- A typed or composer message waits in Codex's own queue for a tool call that never comes.
- A priority message typed into the PTY joins that queue too.

Esc ends the turn at once, and then all of them arrive within a second. The Workspace already
sent Esc for "Interrupt and send", but only on a pending composer entry. Nothing told the person
that the turn had stopped moving.

## What the runtime does

`common/stall-watch.js` tracks progress for each turn. `common/server.js` asks it every 15 s
while a turn runs:

- Interactive: between the prompt hook and the Stop hook, and not while the turn waits for an
  approval or an answer.
- Autonomous and Scheduled: from start to exit, because the whole run is one turn.

Each of these counts as **progress**:

- any turn hook event;
- the transcript file growing (Codex writes each finished item to its rollout);
- the rendered screen changing after the driver's `progressScreen(rows)` has removed what the
  TUI animates by itself.

For Codex that is the status row's bullet (`•`/`◦`) and everything from the `(2m 29s • esc to
interrupt)` parenthesis to the end of the row. When the row is wider than the terminal, Codex cuts
it with `…`, and the cut moves each time the time gets a digit longer. A first browser run
counted that as progress at 59 s → 1 m 00 s, and in production it would happen again at 10 min
and 1 h. The row's header stays: a reasoning summary changes it. A screen change in the
same check interval as keys written to the PTY does not count. Those keys are the person or a
fleet message typing into the queue panel, not the agent working.

After `agent.stalledTurnNoticeMinutes` (default 20) without progress, the activity frame carries
`stalled: {minutes, reason}`. The reason is `reconnecting` when the status row says `Reconnecting...
waiting for network`, otherwise `no_progress`. The Workspace replaces "Working…" with "No
progress for N min: the turn may be stuck" (or "Waiting for the network for N min"). A person
who can write to the session also gets **Interrupt**, which sends the driver's interruption key
the same way "Interrupt and send" does. Codex then submits its queue, and the fleet messages
follow at the turn's end.

After `agent.stalledTurnInterruptMinutes` (default 60), an **Autonomous or Scheduled** run gets
Ctrl+C. The session agent first writes `[agent] Turn interrupted after N min without progress.`
to the scrollback and the pod log. `codex exec` stops on Ctrl+C and the session ends as Failed.
The same sentence leads its termination message, so the session list gives the stall as the
reason instead of the CLI's last lines.

Why Ctrl+C here, and Esc in the Workspace: these runs are `codex exec`, which has no TUI and
reads no keys. Measured on 0.167.0, Esc left a stalled exec run going for 10 s and more, while
Ctrl+C ended it at once. The TUI's own risk, a second Ctrl+C at an idle prompt quitting Codex,
does not exist without a prompt. The interrupt is also latched: one key per stalled turn, only
while the process runs. A run whose last output lands in the check that reaches the limit counts
that output as progress first, and a run that has exited gets no key.
Nobody watches such a run, and a hung one holds its pod and its account for as long as it
lasts. **Interactive sessions are never interrupted for the person**: their notice stays, and
the person decides. 0 turns off either setting. Both are read when a session pod starts.

Only Codex has a `progressScreen` today. A driver without one gets no watch, because a raw PTY
byte count cannot tell a busy turn from a TUI redrawing its timer. For another CLI the hook is
worth adding once its animated rows are measured on the pinned version.

## Alternatives considered

- **Managed Codex config.** The two unbounded waits have no setting. Keep-alive events defeat
  `stream_idle_timeout_ms`, and `request_max_retries` does not apply to `waiting for network`.
  The bounded waits are already bounded at 30 s or 300 s. Lowering `tool_timeout_sec` on our
  MCP servers would cut legitimate long calls and fixes nothing that hung.
- **PTY output as the progress signal.** This is what `activityLastOutput` uses for runtimes
  without hooks. Here it would never fire: Codex redraws its timer every second, so a 17-hour
  stall is "output" all the way through.
- **Interrupting interactive sessions automatically.** An interactive turn may legitimately run
  long with a person watching, and an interrupt discards the work in flight. The notice puts the
  decision with that person. Unattended runs have nobody to decide.
- **Detecting it in the backend.** The hub sees status and hook callbacks, not the screen or the
  transcript size. It would have to guess from silence, and silence is normal in a long turn.

## Measured (Codex 0.167.0, real `server.js`, notice set to 1 min)

- The owner's stall (WebSocket keep-alive): reported 68 s after the stall began. A composer
  message typed meanwhile did not reset the clock. Interrupt delivered the composer, priority
  and normal messages.
- A 4-minute build polled every 20 s: no notice. That is 14 model requests, and each poll's
  output reaches the screen and the rollout.
