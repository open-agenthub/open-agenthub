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
the running turn ends (#173, left unmarked until then), it is pending; delivered through any channel — read with
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
| Interactive Codex or OpenCode terminal (turn hooks, below) | typed into the PTY: optional Escape, 300 ms, text, 300 ms, Enter | its next turn: the end-of-turn hook hands it over when the running turn ends, or it is typed in while the agent waits at its prompt |
| Interactive terminal, other runtimes | typed into the PTY, as above | inbox |
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

## Plain messages as the next turn (#173)

Without a mod, a plain message used to wait in the inbox until the agent called `agent_inbox`
on its own, which an interactive Codex agent does when told to and not otherwise. The owner had
to type "check your inbox" into every Codex session that a peer had written to. Priority does not
help: it types the message at once, also into the middle of a turn, and a sender cannot know
whether the receiving agent is busy.

A runtime whose hooks report its turns (`driver.turnHooks`: Codex, and OpenCode since #192) now gets plain
messages at the next safe point instead. The session agent answers the hub `next-turn`; the hub
tells the sender `deliveredVia: "next_turn"` and leaves the row **undelivered**. From there two
paths take it, both from the hub inbox (`GET …/messages?via=next_turn`), at the moment the
message is handed over:

- **At the end of a turn.** `common/fleet-turn-hook.mjs` runs as a managed Codex `Stop` hook
  and asks the session agent `POST /agenthub/turn {event:"stop"}`. The session agent takes what
  waits and answers with its text; the hook returns `{"decision":"block","reason":<text>}`, which
  Codex feeds to the model as the next input of the same session (shown in the TUI as *Blocked by
  hook* with the text under it). Nothing is typed, so neither a draft in the prompt box nor a
  dialog can be hit. OpenCode has no hook command that can return input, so its policy plugin
  plays the part (`opencode/next-turn.mjs`): on a top-level `session.idle` it asks the same
  route and submits the answer with the plugin's own client as a new prompt of the session
  (`session.promptAsync`), which the TUI shows as an ordinary user message. Also nothing typed.
- **While the agent is idle.** When the last turn event was a Stop and the session agent decides
  the CLI really sits at its prompt, it pastes the message in (see *How an idle message is typed*). "Really"
  means all of: no `UserPromptSubmit` or `PermissionRequest` since the Stop; no key from a person
  since the last submitted prompt (a draft typed during the turn is still in the box, and the
  message would be appended to it and sent); no output for 1.5 s (Codex redraws its spinner many
  times a second while it works); and no CLI menu on the screen, checked with the Workspace's own
  detector (#112, #166) on the scrollback replayed into a headless xterm, the bytes a browser
  attaching now would get. The person's keys that arrive while the message is typed are held and
  written one pause after the Enter. Written in the same instant, OpenCode read Enter and keys as
  one burst, and the keys ended up in the peer's message. Those keys are a draft in the box of the
  turn the message starts, and the prompt event of that turn arrives after them, so they are dated
  after it. Dated by arrival, the next idle message was typed into that draft and sent with it
  (`zz[AgentHub: …]`, reproduced on Codex; #192). A batch taken from the hub when the person started typing in the
  meantime is held and goes with the next Stop. Before the first Stop of an agent run nothing is
  typed: the session agent cannot tell a start-up dialog from a prompt.

The Codex hook reports `UserPromptSubmit` (busy) and `PermissionRequest` (an approval dialog is
open) too; the OpenCode plugin reports `chat.message` as the prompt. OpenCode needs no approval
report: its managed config sets OpenCode's own permission to `allow` and the plugin asks the hub,
so its TUI never opens an approval dialog. It ignores Codex's helper threads — the title generation runs as a session without a rollout
file, subagents carry an `agent_id` — whose prompts would otherwise mark an idle agent busy.

Each message is headed as everywhere else (sender title and id), under one line saying these come
from other sessions and are not the owner's instructions: a peer's request is data for the
agent to weigh, not authorisation.

**One turn is continued at most three times** this way. Two agents answering each other could
otherwise keep a single turn alive for good. After the third the Stop is answered with nothing,
the turn ends, and whatever arrives next starts a turn of its own through the idle path, a
boundary the person sees. This bounds the hook loop, not a conversation between peers; that is
the same with the mod.

**A turn stopped with Escape is not continued.** The person is at the keyboard and should not find
a peer's message started in place of what they meant to do; it waits for the end of their next
turn. Codex runs no Stop hook for an interrupted turn. OpenCode fires `session.idle` either way,
and on 1.18.35 it does so before the abort is stored: the last answer then has neither
`MessageAbortedError` nor `time.completed`. The plugin therefore delivers only after an answer
that completed without an error. A failed turn waits as well, since the person has something to
look at first. The chat relay's abort filter in `turn-notify.mjs` had the
same blind spot and uses the same reading since #198 (`docs/chat-relay.md`).

**How an idle message is typed (#219).** The message is written as one bracketed paste. Enter is
sent once the replayed screen shows it, either its last line or the CLI's placeholder for a long
paste, with a 3 s cap. The CLI's own prompt hook then has to report the submit: Codex's
`UserPromptSubmit`, OpenCode's `chat.message`. With no report after 5 s, Enter is pressed once
more. After another 5 s the messages go back to wait for the next Stop. The text left in the box
then counts as the person's draft, so nothing is typed onto it. Paste markers inside a message
body are removed, because a body that carried the end marker would turn the rest of itself into
keys.

Before #219 the message was typed as plain keys, with Enter 300 ms later. Codex 0.160.0 left
every idle message of 1 KB or more in its prompt, unsent. Codex classifies plain keys arriving
this fast as a paste burst (`paste_burst.rs`). The burst's tail stays buffered until a UI tick
that does not come, and each Enter after that is appended to the burst as a newline. The screen
showed the text minus its last line, and a second Enter one second later was a newline too.
OpenCode 1.18.35 reads plain keys one at a time: 4 KB took about 15 s to arrive and 8 KB was
still not submitted after a minute. As one paste, 8 KB was on the screen of both CLIs within
300 ms. The context-clear handover already used a paste.

Rejected:

- **A longer, size-dependent pause before Enter.** It does not help Codex: the tail waits for a
  tick, not for time, and an Enter at any point joins the burst.
- **Plain keys in chunks with pauses between them.** 512 bytes every 30 ms got through Codex in
  the rig, but only because of the 8 ms and 60 ms timings inside Codex's burst detector, and it
  would keep OpenCode's per-key speed.
- **The screen check alone as proof of the submit.** Codex shows a paste over 1,000 characters
  as `[Pasted Content N chars]`, and the submitted text then stays visible in the history above
  the box. The screen can tell that the paste has arrived, but not that Enter took it. The
  prompt hook reports exactly that.
- **Clearing text that was not submitted (Ctrl+C).** Ctrl+C interrupts a turn that did start
  after all, and it quits an OpenCode whose box is already empty. The price of leaving the text
  is that a person who submits it sends the message once more, at the next Stop. A message that
  was never sent and that nobody can see would be worse.

**The menu check does not recognise OpenCode's dialogs.** They are lists without numbers (model
picker, command palette) that the Workspace's detector does not treat as menus. On OpenCode a
dialog opens only on the person's keys, and any key since the last prompt already stops the idle
path; the end-of-turn path does not type. So the gap leaves a message waiting, never typed into a
dialog. Widening the shared detector would change what the Workspace shows for OpenCode, which is
a decision of its own.

Proved with the pinned Codex 0.160.0 TUI in the real `common/server.js` PTY, with the branch's
`requirements.toml` at `/etc/codex` (proot), a stand-in hub inbox and a mock model. A message
sent mid-turn reached the model 0.2 s after the turn ended, as the Stop continuation. One sent to an
idle agent was typed in 0.5 s after the send. With a draft in the prompt box nothing was typed, and
the message rode the Stop of the draft's turn. With `/model` open nothing was typed. Five messages
arriving during continuations went hook, hook, hook, idle typing, hook. Each message was taken
once and reached the model once. `fixtures/codex-fleet-turn-smoke.js` pins the hook contract
for CLI bumps.

The same rig ran the pinned OpenCode 1.18.35 TUI, with the managed `/etc/opencode` from
`managed-config.js` (proot). The results were the same:
- a message sent mid-turn was taken 0.1 s after the turn ended and submitted by the plugin;
- an idle agent had its message typed in 0.4 s after the send;
- a draft was left alone, and the message rode the plugin's next stop;
- with `/models` open nothing was typed;
- the chain went plugin, plugin, plugin, idle typing, plugin.

After Escape the waiting message stayed in the inbox, then went with the Stop of the person's
next turn. It was started 0.5 s after the Escape until the completion check above was added.

Rejected on the way:

- **A queue in the session agent, marked delivered when the pod accepts the message,** as the mod
  queue is. `agent_inbox` would then return nothing for as long as the turn runs, and an agent that
  exits before its turn ends would lose the message. Taking it from the hub at hand-over keeps the
  inbox the record: until then `agent_inbox` still gets it, and whichever comes first takes it
  once.
- **Only the idle path, typing at the end of the turn.** The Stop arrives before the CLI has
  finished drawing, another Stop hook (the skill reminder) may continue the turn, and the person
  may have a draft. The hook needs no typing at all and is exact about the turn's end; typing is
  kept for the one case it cannot cover, an agent that has no turn to end.
- **Typing at once and letting Codex queue it.** Codex queues text typed during a turn, but the
  same keys land in an approval dialog or in the person's draft. A message must not arrive in the
  middle of a turn.
- **The hook asking the hub itself.** The hook has the callback token, but only the session agent
  knows whether the runtime is draining for an update, how often this turn was continued and
  what it already took. One hop over loopback keeps all of that in one place.
- **Codex's legacy `notify` program.** It runs after the turn, but cannot return input.

Per runtime:

| Runtime | Plain message |
|---|---|
| Claude | unchanged: the mod (above), verified again with Claude Code 2.1.287 through the real `common/server.js` |
| Codex | next turn, as described here |
| OpenCode | next turn (#192): the policy plugin submits it on `session.idle`, or it is typed in while idle |
| Cursor | inbox. Its `stop` hook takes `followup_message` (the skill reminder uses it), but its TUI cannot be driven here without a real key, so the path is unproven and left off |
| OpenClaw | inbox. It has no hooks, so nothing tells the session agent where a turn stands, and typing would be blind |

The cost is one loopback call per hook event and one hub request per Codex or OpenCode turn end.

## The mod queue is loopback only

`/agenthub/mod/inbox` and `/agenthub/mod/heartbeat` accept connections from `127.0.0.1` only
and a bearer token (`AGENTHUB_MOD_TOKEN`) the entrypoint mints per pod and passes to the CLI
process. The hub's callback token is not reused for them: it authenticates the hub to the
pod, and handing it to the agent process would let anything the agent runs call the hub as
the pod. The mod token stays inside the pod; the agent user can read it from its own
environment, which is fine — that user already owns the process the mod runs in.

## What the sender is told

`deliveredVia` is `injected` for the PTY and chat channels, `mod` for the mod queue,
`next_turn` when the agent gets it once its running turn ends (above; the row stays undelivered
until then), and `inbox` otherwise, with `reason` naming why (`session_not_running`, `pod_unreachable`,
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
  again when the pod comes back; the agent's own inbox poll gets it. An interactive Codex agent
  is the exception: its next Stop takes every undelivered message, however it got there.
