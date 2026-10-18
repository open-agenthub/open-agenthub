# Slash commands in the Workspace composer (#169)

Typing `/` in the Workspace composer lists the commands of the session's agent CLI: built-ins,
custom commands and, where the CLI offers them under `/`, skills. The list narrows as the person
types. Up/Down move the highlight, Tab or Enter complete the highlighted command, Enter on a
command that is already typed in full sends it, and Escape closes the list. Commands that switch
the CLI to another conversation show a warning under the composer before they are sent.

## Executing: the terminal, as typed

A command reaches the CLI the way any composer message does: bracketed paste into the agent's PTY,
then Enter (`agent-workspace.md`). Nothing is translated or run by AgentHub itself. This was
checked against every pinned CLI in a PTY, fed the composer's exact bytes:

| Agent | Checked with | Result |
|---|---|---|
| Claude Code 2.1.287 | `/model`, `/login`, `/clear`, a custom command with arguments, a skill | all run as typed (`/model`, `/login` open their menus) |
| Codex 0.160.0 | `/model` | opens its model menu |
| OpenCode 1.18.35 | `/models`, a custom command from its config | opens its model dialog; the command is listed in its own menu |
| OpenClaw 2026.7.1-2 | `/help`, `/model` | both run |
| Cursor (unpinned) | — | not exercised: its TUI does not start without a real login |

The rejected alternative was a structured command API per agent: an SDK call or JSON-RPC
method that runs `/compact` or switches a model without the TUI. An existing open-source agent
chat UI does it that way. It owns the agent process through each provider's SDK or app-server and
re-implements commands like model selection as its own dialogs. AgentHub's sessions run the CLI
itself on a PTY, so the person and the terminal can always take over (`agent-workspace.md`, #114).
A second control channel would need one adapter per CLI. It would also bypass the screen the
terminal shows, so the two could disagree about the CLI's state. Typing the command is what the
terminal does anyway, and every CLI here accepts it.

### Commands that open a menu

`/model`, `/login`, `/resume` and others open one of the CLI's own menus. The composer does not
need to know which ones: the menu is detected from the rendered screen and offered as the menu
notice above the composer (#112, #166), exactly as when it was opened from the terminal. While
that menu is open the composer refuses to send, so a completed command cannot land in the menu as
an answer. A per-command "opens a menu" flag was rejected for the same reason the screen is used
for the notice: it would be a guess about the CLI, kept in step by hand, for something the screen
already shows.

### Commands that switch the conversation

`/clear` in Claude Code starts a new session with a new transcript file (checked with 2.1.287: the
transcript after `/clear` has a new session id). The Workspace follows the session's own transcript
(`transcripts.md`), so it keeps showing the conversation from before the command, and the next
turns appear only in the agent terminal. A session restart resumes that first conversation too,
because the resume names its id. Following the switch is #174. The catalogs mark such commands (`effect: "new-conversation"`):

- Claude `/clear`, `/resume`, `/branch`
- Codex `/clear`, `/new`, `/resume`, `/fork`
- OpenCode `/new`, `/sessions`
- Cursor `/clear`, `/resume`, `/fork`
- OpenClaw `/new`, `/reset`, `/session`, `/sessions`

The composer warns before sending them. It does not block them, because the command does what it
says in the CLI. Only Claude's `/clear` was observed. The others are marked from what the CLI's
own menu says they do. Following the new conversation needs the session agent to switch
transcript and resume id when the CLI does (#174).

## Listing: a recorded catalog plus what is on disk

None of the five CLIs reports its commands on the PTY it runs on. The list therefore has two
parts, assembled by the session agent (`GET /agenthub/commands`, `common/slash-commands.js`):

1. **Built-ins, recorded per pinned CLI** in `agent-runtime/<agent>/slash-commands.js`. They were
   read off each CLI's own "/" menu in a PTY. Cursor's came from the command table in its bundle,
   because its TUI needs a login. Bumping a CLI means reading its menu again
   (`agent-runtime-updates.md`).
2. **Custom commands and skills from disk**, read where the CLI reads them, on every request so a
   skill synced or written during the session appears:

| Agent | Custom commands | Skills under `/` |
|---|---|---|
| Claude | `.claude/commands/**/*.md` in the workdir and HOME (`sub/name.md` is `/sub:name`) | `.claude/skills/*/SKILL.md` in the workdir and HOME, except `user-invocable: false` |
| Codex | none: 0.160.0 lists no prompt files under `/` | none: skills are invoked with `$name`, and its popup takes the composer's Enter as "insert", not "send" |
| OpenCode | `command` in its user and project `opencode.json`, `.opencode/command(s)/*.md`, `~/.config/opencode/command(s)/*.md` | none: not part of its `/` menu |
| Cursor | `.cursor/commands` and `.claude/commands` in the workdir and HOME | not offered: unverified without a login |
| OpenClaw | none | none |

Descriptions and argument hints come from the files' frontmatter (`description`, `argument-hint`).
A built-in wins over a custom command with the same name, because that is the one the CLI runs.
Names that a CLI could not take after its `/`, or that would type a space or control character
into the composer, are dropped. The scan skips symlinked command files. It reads only the head of
each file and stops at 400 entries, marking the answer as truncated.

Rejected alternatives:

- **Asking the CLI for its list.** Claude Code reports its commands in the init message of its
  headless stream-json mode. That would mean a second CLI process per request beside the
  interactive one. Typing `/` into the running TUI and scraping its menu would edit the person's
  input box.
- **A static list in the frontend only.** It cannot see custom commands or skills, which exist
  only in the pod.
- **Offering skills mid-message or as `$name`.** Every CLI here runs a command only when the
  message begins with it, so the list opens only for a `/` at the start of the message. Codex's
  `$` popup swallows the Enter the composer sends after the paste, so the skill would sit unsent in
  its input box.

The backend proxies the route as `GET /api/sessions/{id}/commands`, to the stored pod address with
the session's callback token, the same way as the diff reader (`workspace-diff.md`), and with the
same access as Files: the list names command and skill files that anyone who may read the
session's files can already see. Only owners and collaborators have a composer to use it in.
The answer is not cached (`no-store`). The browser keeps it for 30 seconds per session, and retries
after 3 seconds while the pod is still starting.

## Limits

- An older runtime without the route, a stopped pod, or a shared link (which has no proxy route)
  gets no completion. Typed commands are still sent.
- Account-dependent built-ins (Claude's bundled skills, plan-only commands) are not listed. They
  still run when typed.
- The menu notice does not yet recognise a menu that a narrow terminal wraps. At 390 px the
  drawer's 47 columns made Claude's `/model` menu wrap its option lines, and the notice did not
  appear, though the menu was open in the terminal. That belongs to the menu detection (#166),
  not to this list.

## Verification

- Session agent: catalog validity for all five runtimes, discovery (frontmatter, namespaces,
  hidden skills, unsafe names, symlinks, shadowing, the bound), the route's authentication and
  methods, and the real common server with the real Claude catalog over TCP
  (`session-agent/test/slash-commands.test.js`).
- Backend: access per role, 503 for an unavailable pod, and the proxy's pod address, token and
  size bound (`SessionCommandsTests`). A throwaway console ran the real `SessionCommandsReader`
  against a running session agent: the right token returned the list with the custom command,
  a wrong one returned nothing.
- Frontend: ranking, completion, the start-of-message rule, keyboard and mouse, the warning and
  the fallbacks (`lib/slash-commands.test.js`, `workspace/composer-slash.test.js`).
- End to end in Chromium at 1280 and 390 px: this branch's web app, through a stand-in hub to
  this branch's session agent running the real Claude Code 2.1.287 against a mock model. Typing
  `/` listed 53 commands. `/dep` + Tab + `prod` + Enter sent `/deploy-check prod`, and the model
  received the expanded command. `/rig` + Enter + Enter ran the skill. `/mod` + Tab + Enter opened
  the model menu, which the menu notice showed at 1280 px. Escape closed the list, and `/clear`
  showed the warning.
