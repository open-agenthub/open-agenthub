# Claude Code mods in the Claude runtime

## What a mod is

Since Claude Code 2.1.287 a plugin can carry a *hooks module*: an ES module whose
`register(on)` subscribes JavaScript functions to the CLI's own events — `tool.call`,
`prompt.submit`, `turn.start`/`turn.complete`, `command.run`, `ui.render` and so on — and
calls back into the CLI through a mods API (`$.prompt.submit`, `$.turn.abort`,
`$.clock.every`, `$.http.fetch`, `$.ui.status`, …). The functions run *inside* the CLI
process, in every kind of session including `claude -p` and the Agent SDK (only the drawing
parts need a terminal). Anthropic documents them under
[code.claude.com/docs/en/plugins/mods](https://code.claude.com/docs/en/plugins/mods/overview);
the complete contract is the `claude-code.d.ts` the CLI writes beside a loaded mod.

Three things distinguish a mod from the settings hooks the runtime already uses
(`claude/hooks/*.sh`, wired by `mcp-policy-hook.sh --settings`):

- A settings hook is a *process* the CLI runs at a lifecycle point and reads an answer from.
  A mod is a *function* that can also start a turn, end one, add a command, or draw.
- A mod can run work between events (`$.clock.every`), where a settings hook only exists
  while its event is being handled.
- A mod is statically analysable: `claude plugin validate` lists every event it hooks and
  every `$` method it calls without running it, which is why the API has the rules it has
  (calls written in full as `$.ns.method`, event names as string literals, imports only from
  the plugin directory, no Node APIs or timer globals in the hooks module).

Mods load from `--plugin-dir` or, for a process you cannot pass a flag to, from
`CLAUDE_CODE_PLUGIN_DIRS`. `--safe-mode`, `--bare` and `disableAllHooks` turn them off; the
runtime sets none of those.

## The fleet mod, and why it is a mod

`agent-runtime/claude/mods/agenthub-fleet` exists for one job that nothing else in the
runtime can do: put a message into a running Claude session's conversation, now, in every
mode. `docs/priority-messages.md` explains the message flow; this is about the mechanism.

The session agent can type into the PTY, which serves an interactive terminal session and
nothing else. A chat-UI session runs on pipes, where the session agent writes stream-json
`user` events — that works, but only for chat. An autonomous `-p` run has neither: its prompt
is an argument, its PTY is written by nobody. A settings hook cannot help, because the only
settings-hook moment that could carry a message in is `UserPromptSubmit`, which fires when a
prompt is submitted — the thing we are trying to cause. A mod has `$.prompt.submit`, which
starts a turn from outside any event, and `$.turn.abort`, which ends the running one, and
both work under `-p`. That is the whole case.

The mod is small on purpose. It polls `http://127.0.0.1:7681/agenthub/mod/inbox` every three
seconds with the token the entrypoint minted (`AGENTHUB_MOD_TOKEN`); the poll is also the
heartbeat that tells the session agent a mod is alive, so it queues there instead of typing.
Priority messages are submitted as prompts, after `$.turn.abort` when an interrupt was asked
for and a turn is running (the id comes from `turn.start`, cleared by `turn.complete`). Plain
messages set a status line, are printed by `/inbox`, and ride along as `context` with the
next prompt the person sends. Its logic without the mods API sits in `hooks/lib.mjs`, so the
session-agent node suite tests it; the hooks themselves are tested with `claude plugin test`,
which the image build runs.

The mod polls rather than being pushed to because a mod cannot listen: it has `$.http.fetch`
and no server. Three seconds is the latency a person perceives as "now" for a message typed
elsewhere, and 20 loopback requests a minute cost nothing.

Seen working end to end (Docker Desktop, Claude Code 2.1.287, interactive terminal session): a
priority message sent through `POST /api/sessions/{id}/messages` came back as
`deliveredVia: "mod"`, the TUI printed Claude Code's own notice that a plugin submitted the
prompt between turns, and the model started the turn on it; a plain message showed up as the
status line `agenthub-fleet: 1 fleet message — /inbox` under the prompt. Not yet seen in a
cluster: the same in a `-p` chat-UI session, and an interrupt aborting a running turn.

### The usage hook

The same mod carries the one other thing only a mod can see: the plan's rate-limit windows.
Claude Code fires `session.measure` after every turn and whenever a window moves a whole
point, with the figures `$.session.usage()` returns (`rateLimits: [{kind, percentUsed,
resetsAt}]`). The mod posts every window at or past `AGENTHUB_LIMIT_THRESHOLD` (default 100)
to the session agent's `POST /agenthub/mod/limit`, once per window and reset time, and reads
the usage afresh after a `turn.complete` with `reason: "error"`, the turn an API refusal ends.
The session agent relays the report to the hub, which marks the account and may move the
session to another login — `docs/account-limits.md` has the policy and why the mod holds
none of it. `turn.step` was not used: its hook is a generator over the streamed response and
its result carries no error text, while the windows themselves come from response headers
that `session.measure` already pushes.

## Where it lives and how it loads

The plugin directory ships inside the runtime tree (`/opt/session-agent/claude/mods/…`) rather
than beside it. A custom image gets the runtime copied to `/opt/agenthub/session-agent` by
the init container, and a path baked into the image's `ENV` would point into nothing there;
the entrypoint therefore sets `CLAUDE_CODE_PLUGIN_DIRS` from `$RUNTIME` at start, and the
Dockerfile's `ENV` only covers the plain image. The token is minted in the same place and
inherited by the session agent (which guards the queue with it) and by the CLI (which polls
with it). The hub's callback token is deliberately not reused: it authenticates the hub to the
pod, and the CLI process — and every tool it runs — must not be able to speak to the hub as
the pod.

The Dockerfile runs `claude plugin validate --strict` and `claude plugin test` on the directory
at build time. Both are offline, and both run against the pinned CLI, so the version bump
that renames an event or a method fails the image build instead of shipping a mod that
silently hooks nothing. `update-agent-runtimes.yml` opening a PR for a new CLI version
therefore gets a red check when the mods API moved.

## What we deliberately do not do with mods

**Permission relay stays a settings hook.** It is tempting to move `pretooluse-hook.sh` into
the mod as a `tool.check` hook: no shell, no curl, one process. We do not, because of where
`tool.check` sits. It fires *after* the permission rules and the settings hooks have decided,
and a mod's answer replaces theirs — a user-installed mod can turn a `deny` into `allow`
(the docs list the cases under "Extend permissions with hooks", and the managed-settings
option `allowModsToOverrideDenyRules` exists precisely because this is possible). The
runtime's deny rules are the boundary when auto-approve is off (`CLAUDE.md`, "the session
allow list is a head start, not a boundary"); the relay must not be the component that can
step over it. A `PreToolUse` settings hook cannot, so the relay stays one. The same
reasoning keeps the MCP sharing policy where it is.

**No `tool.call` hooks at all.** A `tool.call` hook that throws or times out is skipped and
the call proceeds, and one that answers without `next` keeps the settings hooks from running.
The fleet mod has no business near tool calls, and the cheapest way to be sure it cannot
interfere with permissions is to not subscribe.

**No drawing.** Panes and bands only show in a terminal, and the runtime's terminal is a PTY
the web app streams; a band above the prompt would be rendered into the scrollback the hub
stores. The status line (`$.ui.status`) is the one visual the mod uses, and it is one line.

## Looking ahead

Things a mod can see that nothing else in the pod can, noted so they are not forgotten:

- `$.session.usage()` as a live readout. The usage hook above posts a window only when it
  is used up; posting the figures on every `session.measure` would give the session view a
  "context 62 % · weekly limit 40 %" line without parsing a transcript.
- `ui.render` on `AskUserQuestion` and the permission dialog could mirror a pending question
  into the hub's permission card with its real options, rather than the text the
  `Notification` hook scrapes. The permission *decision* would still come from the
  settings hook, for the reason above; only the display would move.

Neither is built. Both would be new hooks in this mod, with the same offline-tested shape.
