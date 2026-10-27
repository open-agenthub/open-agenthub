# When a session stops "starting"

A just-created session shows "Session starting" in the Workspace with the stage it is in:
"Waiting for the session's pod to start…" while the pod is Pending, then "Starting the agent…"
until the agent can take input, after which an interactive session invites a task and an
autonomous one says "The agent is starting its task…" until its first output.

#241 ended "Starting the agent…" when the session agent answered the activity subscription with a
known state. Its proof stood in for the session agent; the real one answers with the CLI's turn
state, and for a runtime whose hooks report its turns (Claude, Codex, OpenCode) that state is
`unknown` until the CLI's first hook. An idle CLI fires none before somebody types, so the
Workspace kept "Starting the agent…" over a CLI that had taken input for seconds — until the first
transcript entry, which needs a turn (#306).

## The signal: `ready` in the activity answer

The session agent adds `ready` to every activity frame: the CLI process runs (and has not exited,
is not being restarted for a resume or an account switch, and the pod is not draining for a
runtime update). It is independent of the turn state, which stays `unknown` until a hook says
otherwise. The Workspace ends "Starting the agent…" on `ready`; a runtime that does not send it
still ends the stage on a known state, as before.

When a CLI is ready is measured, not assumed:

| CLI (pinned) | Keys typed into the PTY before the CLI has drawn anything | `ready` |
|---|---|---|
| Claude Code 2.1.287 | kept: they are in its input box once it is drawn | at the spawn |
| Codex 0.160.0 | kept | at the spawn |
| OpenCode 1.18.35 | **lost** until its input box is drawn, about 2.7 s after the spawn; typed at 3.0 s they are kept | once this start's own output shows the input box |

OpenCode's driver names its input box (`inputReadyScreen`, the same predicate `/clear` already
waits for); the session agent renders the CLI's output with the headless terminal and reports
`ready` when the box appears. A driver without it is ready at the spawn. An OpenCode session that starts on its sign-in screen
(no credential) therefore keeps "Starting the agent…" above the sign-in card until the login is done
and the box is drawn, as before this change; Claude and Codex show the card with the invitation.
`scripts/proofs/306/ready-input.cjs` types a marker over the agent socket the instant `ready`
turns true: all three CLIs keep it; with OpenCode's predicate removed, `ready` came at the spawn and
the marker was lost.

- *Rejected: report the turn state as `idle` until the first hook.* The turn state is what fleet
  delivery types into the CLI on (`docs/priority-messages.md`), and a session started with a task is
  working, not idle, before its prompt hook arrives. Readiness and turn state are different facts.
- *Rejected: wait for the CLI's prompt on the screen for every CLI.* Claude and Codex read input
  before they draw, so that only delays the invitation, and each CLI's screen is a guess the next
  version can break. Only the CLI that loses input gets a screen check.
- *Rejected: Claude's SessionStart hook.* It fires after the prompt is drawn (below), Claude only.

## Where the time goes

`scripts/proofs/306/startup-timeline.cjs` runs the shipped `claude/entrypoint.sh` and
`common/server.js` with the pinned Claude Code; times are from the entrypoint's start, i.e. the
container's start. Login and network vary per scenario: a fake subscription login against the real
network, the same login through the local fake identity, an API key against a local Messages
endpoint, and no credential at all.

| | subscription | fake identity | API key | none |
|---|---|---|---|---|
| CLI spawned in its PTY | 269 ms | 280 ms | 270 ms | 244 ms |
| agent answers, `ready: true` | 317 ms | 315 ms | 319 ms | 273 ms |
| CLI's first output | 607 ms | 465 ms | 657 ms | 427 ms |
| input box drawn | 699 ms | 567 ms | 801 ms | 523 ms |
| SessionStart hook | 848 ms | 837 ms | 694 ms | 738 ms |
| turn state leaves `unknown` | first turn | first turn | first turn | first turn |

Before the CLI spawns, the entrypoint restores the login, syncs skills and writes the MCP and hook
configuration (about 250 ms here; a resumed session also downloads its state archive). The pod's own
start — scheduling, image pull, the clone init container — comes before all of this and is the
"Waiting for the session's pod to start…" stage; it was not measured here, there is no cluster in
this proof.

`scripts/proofs/306/browser-check.mjs` opens the web app in Chromium before the container starts
and samples the conversation area every 50 ms. On this branch it went from "Starting the agent…"
to the invitation 0.36–0.84 s after the container start, and a task typed into the composer reached
the model through the CLI 0.5 s later. With `main`'s runtime and web app it still said "Starting
the agent…" 40 s later.
