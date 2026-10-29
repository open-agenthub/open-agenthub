# Clearing context with a handover

A long-running coordinator can call the in-pod `context_clear({ followUp })` tool to
continue with fresh model context. Write a short self-handover in `followUp`: current
state, decisions that still matter, and open tasks. The result acknowledges scheduling;
finish the current turn naturally. The tool call itself belongs to that turn, so clearing
inside the tool handler would interrupt its own result and lose the handover.

The runtime queues one context action in the same safe-point queue used by next-turn fleet
delivery. It waits for a completed turn, no unsent owner draft, quiet terminal output and
no visible CLI menu. It reserves input across the native slash command and the handover,
using the same input holding that protects fleet delivery. Claude and Codex use `/clear`;
OpenCode uses `/new`. Claude must report a different conversation identity before the
follow-up is sent. Codex creates its rollout only when the first message is submitted, so
its fresh native welcome screen with a sole empty composer confirms `/clear`; waiting for
a rollout first would deadlock the handover. The shared quiet-output/menu check also runs
after the command.
A Stop-hook continuation was rejected: it keeps the old conversation alive rather than
starting a fresh one, and issuing `/clear` as a tool command cannot safely replace the
interactive CLI's context.

Claude's native prompt, permission and Stop hooks and OpenCode's top-level message/idle
plugin events feed that shared queue. They do not take fleet inbox messages: Claude keeps
its mod delivery and OpenCode keeps its existing inbox behavior. A queued clear takes
precedence over a fleet Stop continuation; peers remain in the inbox until the handover
starts the next turn. Mod inbox delivery also waits while the clear action reserves input,
so the handover is the first new message. Its fixed header makes a follow-up beginning
with `/` ordinary message text, rather than another CLI command. Terminal control bytes,
empty handovers and handovers longer than 16,000 characters are rejected.

Only the callback token for the session being cleared can call its internal route. The
in-pod tool accepts no session target and rejects extra fields; there is deliberately no
peer/descendant clear route. An owning user may use the UI API, personal-token API or
owner MCP tool with a session ID. Shared access and parent/child relationships grant no
clear permission. An owner who wants a parent to control a child must use an explicit
owner credential; ancestry alone is never authority. Runtime forwarding verifies the
current pod identity before sending its callback token and does not follow redirects.

The Workspace retains previous conversations above a visible **Conversation cleared**
boundary, while native CLI context and resume target the fresh conversation. Memory,
skills, repository files and the CLI process remain in place. A second request while one
is scheduled returns a conflict. A clear that could not even be typed (the agent was
restarting or the runtime updating) never sends the handover into the old context. Unsupported providers,
autonomous sessions and streaming chat mode reject the operation; their CLI control
paths have not been proved equivalent to an interactive clear.

## The handover is never dropped (#336)

On hub 1.4.0 a session "knew nothing" after a clear. The handover lived only in the session
agent's memory and was typed once, without a check: a readiness wait that timed out after the
clear, an input the CLI dropped while redrawing, or a restart between clear and handover each
left a fresh conversation without it, and only one terminal line said so. Codex added a second
way: in a git checkout its `/new` asks "Where should the new conversation run?" (current
checkout or a new managed worktree). The menu blocked the readiness check, the handover was
dropped, and the owner's next message, which Codex had queued during the clearing turn,
became the first message of the fresh conversation.

What happens now:

- The handover is written to `agenthub-context-handover.json` in the agent's state directory
  before the request is acknowledged, and the next archive tick uploads it. It is removed only
  once the CLI's own transcript holds it. An agent start that finds it (an in-pod restart or a
  new pod resuming the archive) clears again and sends it: the restarted CLI resumes the
  conversation it last saved, which is the old one when the fresh one was never written to.
- Delivery is verified against the transcript, not the prompt hook. The hook says that
  something was submitted, and a queued owner message is submitted too. The first lines of the
  handover must appear in the transcript; one that differs from the pre-clear transcript is a
  fresh conversation.
- Up to three attempts, each after the input looks ready again (quiet output, no CLI menu). A
  retry whose earlier paste is still on the screen presses Enter alone; pasting again would
  put the handover into the prompt box twice. After that it is typed at the next safe point,
  the same queue fleet messages use, for up to three more rounds, and then waits for the next
  agent start. A handover is never typed into an open menu: the menu check is part of every
  readiness wait.
- A clear that is not confirmed (Claude's conversation id did not change, or Codex's welcome
  screen did not appear) still gets the handover. Holding it back loses it when the clear did
  happen; sending it into a conversation that was not cleared repeats known state.
- Each failed round is logged and shown in the Workspace (a `handover` control frame, replayed
  to reconnecting viewers, drives a banner) until the handover is delivered.
- Codex clears with `/clear`, which starts the same fresh, resumable thread as `/new` but never
  asks where to run. In Codex 0.160.0 the managed requirement `[features] worktrees = false`
  suppressed `/new`'s question. Codex 0.165.0 removed that feature flag: it warns that it
  ignores the requirement and asks in every git checkout, and no setting turns it off. The
  context clear then failed exactly as before. Answering the menu with keys was rejected
  again: the two versions already word and order it differently, and a misread menu would get
  the handover typed into it. The requirement is gone, so a person's own `/new` asks; the
  session's workspace is the checkout either way, and agents can still run `git worktree`.
- The tool result says that the handover is saved and arrives as the first message after the
  turn ends.

The rig in `tests/ContextClearProof` runs the real pinned CLIs in the real session agent with a
PTY fault wrapper (dropped first input, 20 s of output after the clear, the pod killed one second
after the clear) and compares main with the branch. Main lost the handover in each fault case and
in Codex's default case. The fix delivered it as the first user turn of a fresh transcript, exactly
once, for Claude Code 2.1.287 and Codex 0.160.0 alike (PR #337 has the table).

## Verification

Real pinned Claude Code 2.1.287 and Codex 0.160.0 called the actual in-pod MCP tool,
through the actual internal controller and HTTP forwarding service, into the real session
server and PTY. A recording model delayed its final answer after the scheduling result:
Claude switched identity 2.03 seconds after that answer, and Codex 4.43 seconds after it.
Both first new model requests contained the handover and excluded the previous task;
the native new transcripts contained the handover and the combined Workspace transcript
retained the previous conversation and boundary. Synthetic session storage and a local
recording model replace external credentials in this proof. The OpenCode native `/new`
transition was proved for #174; its new MCP tool flow has not been exercised end to end.

Authorization tests reject parent and unrelated callback tokens and shared user access.
A real HTTP forwarding test verifies current pod identity before sending the token.
Real stdio MCP tests reject extra target fields. Runtime queue tests cover active turns,
quiet output, menus, duplicate scheduling and failure without handover delivery.

A real Codex `/model` menu remained open for four seconds after a clear request; the
thread ID stayed unchanged. After closing the menu and completing the next owner turn,
the queued clear ran and the handover was the first message in another new thread.
Conservative draft protection keeps a request queued until a submitted prompt resolves
any owner keys entered while a menu was open.
