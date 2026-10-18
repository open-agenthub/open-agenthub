# Priority messages between sessions

## The gap this closes

The project agent fleet (`docs/superpowers/specs/2026-09-20-project-agent-fleet-design.md`)
delivers messages by pull only: a peer stores a row in `session_messages`, and the receiving
agent sees it when it next calls `agent_inbox`. That is right for an autonomous worker that
loops on its inbox, and useless for the other case the fleet was built for — a reviewer that
wants the coder to *stop what it is doing and look at this now*, or a person who wants to
hand a running chat-mode session a note without a terminal to type into. Codex's TUI has
the thing we want: type while the agent works and the text queues as the next turn.

A message can now be sent as **priority**: it is still stored, but the hub then pushes it
into the running agent instead of waiting for the poll. **interrupt** additionally stops the
agent's current turn first and implies priority. The sender learns where the message went
(`deliveredVia`: `inbox`, `injected`, `mod`) and the same row records it (`delivered_via`),
so the web app can say "delivered to the agent" or "waiting in inbox" instead of guessing.

## The path

```
agent_send / POST …/messages          backend                       session pod :7681
  {priority, interrupt}  ──►  store row  ──►  SessionMessageDelivery ──►  POST /agenthub/messages
                                               (Running + PodIp only)        │
                                                                             ├─ mod alive?      → queue for the mod   → "queued-for-mod"
                                                                             ├─ chat pipe?      → stream-json user    → "chat"
                                                                             ├─ interactive PTY → typed, Esc first    → "pty"
                                                                             └─ otherwise       → "unavailable"
                                               ◄── mark delivered_via = injected | mod, or leave the row for agent_inbox
```

Every send surface takes the same two flags: the in-pod MCP (`agent_send {to, message,
priority?, interrupt?}`), the stdio MCP (same, with `projectId`), the remote MCP (`"true"`
strings, as its other flags), `POST /api/remote/sessions/{id}/messages`,
`POST /internal/sessions/{id}/messages`, and the new owner endpoint
`POST /api/sessions/{id}/messages` behind the "✉ Messages" panel in the session view. All of
them store first and push second (`AgentMessageDispatch.PushAsync`), so a pod that is gone
between the two steps costs nothing: the row is still there for the inbox, and the sender is
told `inbox` with a reason.

## What the person sees

Incoming messages are not drawn over the terminal. The first version stacked one banner per
message above the agent's output, and a fleet of chatty peers covered the terminal until
nothing underneath was readable. The "✉ Messages" button in the session header now carries a
badge with the number of messages the panel lists, and the panel opens on demand: the send
form (only while the session is live — there is no pod to push into otherwise) and one tile per
message with its kind (message, priority, interrupt), sender, time and a dismiss cross. A body
longer than three lines is clamped with a *Show more* toggle, so a wall of text from one peer
does not push the others out of view.

What the panel lists — and therefore what the badge counts — is every message the agent has not
received yet: the row has no `delivered_at`. Waiting in the inbox, or accepted by the pod for when
the running turn ends (`next_turn`, #173), it is pending; delivered through any channel — read with
`agent_inbox`, typed into the terminal, submitted by the mod, handed over at the turn's end — it is
not. The badge counts exactly the tiles behind it; an earlier version counted only undelivered
messages while the panel showed more, and a badge of zero over three tiles made people hunt for
the missing one.

The panel used to keep a priority message for five minutes after it was pushed, so the person
could see what interrupted their agent. That read as an unread message: the owner of a Claude
session saw *Priority message from agent …* for a message the mod had already submitted and the
agent had acted on, while its inbox was empty (#186). What the agent received is now shown where it
belongs, in the Workspace conversation, as a message from its sender rather than as the person's
own words (`agent-workspace.md`, "Pending messages"), so the grace period has nothing left to do. Dismissing is local to the browser and never consumes the inbox: the agent's
`agent_inbox` call still returns the message, because a person tidying their view must not eat
a peer's instruction.

The session agent decides the channel, not the hub, because only the pod knows how its agent
runs. The hub reads one word back:

| Agent runs as | Priority message | Plain message |
|---|---|---|
| Claude with the fleet mod alive (`docs/claude-code-mods.md`) | queued for the mod, which submits it as the next prompt (aborting the running turn on interrupt) | interactive: queued for the mod, which submits it as a prompt of its own once no turn runs; during a turn it waits on the status line and goes with the person's prompt if one comes first; autonomous: inbox |
| Chat UI (`claude -p --input-format stream-json`) | written as a `user` event, after a `control_request interrupt` when asked | inbox |
| Interactive terminal (any runtime) | typed into the PTY: optional Escape, 300 ms, text, 300 ms, Enter | inbox |
| Autonomous or scheduled `-p` run without a mod | `unavailable` → inbox | inbox |

The typing sequence is the one `AgentTerminal.SendInputAsync` already uses for chat replies,
verified against the Claude and Codex TUIs (`docs/chat-relay.md`): a fast burst reads as a
paste and a `\r` inside it becomes a newline in the input box, so Enter goes out alone after
a pause. It lives once, in `agent-runtime/common/terminal-inject.js`, with the Escape added
in front for interrupt and another pause after it, because the TUI needs a moment to cancel
the turn before it accepts new input.

Each message is headed `[AgentHub message from agent "<title>" (<id>)]` (or `… from outside
the fleet` for a person or API token), so the agent can tell a peer's instruction from its
owner's, and so a transcript reader can too.

## Why not only the terminal

Pushing everything through the PTY was the obvious first design — the relays already do it
— and it fails for exactly the sessions that need it most. A chat-UI session has no PTY: the
CLI runs on pipes with stream-json on both ends. An autonomous `-p` run has a PTY that
nothing reads; keystrokes into it are lost, and there is no prompt box to queue a message
into. Only an interactive terminal session is served by typing, so the PTY is the fallback,
not the mechanism.

## Why not only the mod

A Claude Code mod can submit a prompt and abort a turn from inside the CLI, in every mode
including `-p`, which makes it the best channel Claude has. It is also Claude's alone: Codex,
Cursor and OpenClaw have no equivalent. A design that relied on it would leave three of the
four runtimes with pull only, and the fleet is meant to mix them. So the session agent keeps
the terminal and chat channels for everyone, and prefers the mod only where one is alive.

## One channel per message

The pod hands a message to exactly one channel. A mod that is alive wins, decided by a
heartbeat: the mod polls `GET /agenthub/mod/inbox` every 3 s, and a poll (or a
`POST /agenthub/mod/heartbeat`) within the last 15 s counts as alive. Without that rule a
Claude session would get the message twice — once typed into its prompt box by the session
agent and once submitted by the mod — and an interrupt twice. The 15 s window is five polls,
long enough that a slow turn does not demote the mod, short enough that a crashed CLI does
not swallow messages: once the heartbeat is stale the session agent types into the PTY again,
and anything still in the mod queue was already answered `queued-for-mod` and marked
delivered, which is the one window where a message can be lost. It is the same window the
PTY has (text written into a process that dies before reading it), so it is accepted rather
than fenced with acknowledgements.

Plain messages reach the mod only in interactive sessions. An autonomous fleet agent looks
for its tasks with `agent_inbox`, so there they stay in the inbox, where that call finds them;
the change below leaves this routing as it was.

### A plain message the mod took is shown without anyone typing

The first version held plain messages in the mod until the person sent their next prompt and
attached them to it as context. The session agent had answered `queued-for-mod` the moment
the mod polled, so the hub marked them delivered, and `agent_inbox` returned nothing — but an
idle agent nobody typed to never saw them (#98). That is the common case the fleet exists
for: a coordinator waiting for its children's reports.

The mod now submits them as a prompt of its own as soon as no turn runs: on the poll that
fetched them when the agent is idle, otherwise on the first poll after the running turn ends
(at most 3 s later). While a turn runs they stay on the status line, `/inbox` still prints
them, and a prompt the person sends meanwhile still carries them as context — whichever comes
first takes them, once. A submit Claude Code refuses puts them back on that list for the next
idle poll. They are framed as the plugin's message, not as the person's words, so the model
can tell a peer's report from its owner's instruction.

Rejected on the way:

- **Leaving plain messages in the inbox for mod sessions too.** Nothing would be marked
  delivered early, but an idle agent does not call `agent_inbox` either, so the report would
  wait just the same — only invisibly, without the status line.
- **Submitting them at once, also during a turn.** Claude Code would queue the prompt behind
  the running turn, which works, but costs a turn of its own for a message that can ride along
  with the person's next prompt when there is one. Holding them only while a turn runs keeps
  that, and the poll after the turn closes the gap.
- **An acknowledgement from the mod before the hub marks the row.** The mod cannot know the
  model read a prompt any better than the engine's `prompt.submit` resolving tells it, and
  the window this would close — the CLI dying between the poll and the submit — is the one
  the section above accepts for every channel. It would add a second round trip and a backend
  state for no case this issue saw.

## The mod queue is loopback only

`/agenthub/mod/inbox` and `/agenthub/mod/heartbeat` accept connections from `127.0.0.1` only
and a bearer token (`AGENTHUB_MOD_TOKEN`) the entrypoint mints per pod and passes to the CLI
process. The hub's callback token is not reused for them: it authenticates the hub to the
pod, and handing it to the agent process would let anything the agent runs call the hub as
the pod. The mod token stays inside the pod; the agent user can read it from its own
environment, which is fine — that user already owns the process the mod runs in.

## What the sender is told

`deliveredVia` is `injected` for the PTY and chat channels, `mod` for the mod queue, and
`inbox` otherwise, with `reason` naming why (`session_not_running`, `pod_unreachable`,
`non_interactive`, `not_priority`, `agent_exited`, …). The row's `delivered_via` is set the
same way by the push, and to `inbox` by `agent_inbox` when it takes the row; whichever
happens first keeps the row (`MarkDeliveredAsync` only touches an undelivered row), so a
push racing a poll never reports a delivery that did not happen.

The web app shows a priority message in the accent colour with that label, and keeps it on
screen for five minutes after delivery: the person would otherwise never see what
interrupted their agent, because a pushed message is delivered — and therefore hidden by the
old "undelivered only" rule — the moment it arrives.

## Not done

- No acknowledgement from the agent that it *read* the message. `delivered_via` says which
  channel took it; a `-p` run that exits before its mod submits the prompt, or a TUI killed
  mid-paste, loses it like a dropped keystroke.
- Peers can interrupt each other freely. The peer rule (same owner, same project or own
  parent/child line) is the only gate, as it is for plain messages; an interrupt from a
  misbehaving peer costs one turn, not the session.
- No retry. A message that lands in the inbox because the pod was unreachable is not pushed
  again when the pod comes back; the agent's own inbox poll gets it.
