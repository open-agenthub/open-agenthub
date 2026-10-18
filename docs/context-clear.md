# Clearing context with a handover

A long-running coordinator can call the in-pod `context_clear({ followUp })` tool to
continue with fresh model context. Write a short self-handover in `followUp`: current
state, decisions that still matter, and open tasks. The result acknowledges scheduling;
finish the current turn naturally. The tool call itself belongs to that turn, so clearing
inside the tool handler would interrupt its own result and lose the handover.

The runtime queues one context action in the same safe-point queue used by next-turn fleet
delivery. It waits for a completed turn, no unsent owner draft, quiet terminal output and
no visible CLI menu. It reserves input across the native slash command and the handover,
using the same input holding that protects fleet delivery. Claude uses `/clear`; Codex and
OpenCode use `/new`. Claude must report a different conversation identity before the
follow-up is sent. Codex creates its rollout only when the first message is submitted, so
its fresh native welcome screen with a sole empty composer confirms `/new`; waiting for
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
is scheduled returns a conflict. A failed clear or readiness check never sends the
handover into the old context and shows a terminal notice. Unsupported providers,
autonomous sessions and streaming chat mode reject the operation; their CLI control
paths have not been proved equivalent to an interactive clear.

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
