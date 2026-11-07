# Switching the model of a running session (#478)

#461 added the account-aware model list to the New/Edit/Duplicate dialogs, and a changed model
applied on the next restart. Changing the model in the middle of a conversation still meant typing
the CLI's own command in the terminal, with a different syntax per CLI. This file records what each
pinned CLI actually does when the model changes mid-conversation, and why the Workspace picker
switches the way it does.

## What each CLI does

Each row was run against the pinned CLI in a real PTY (`tests/ModelSwitchProof/cli-switch.mjs`),
with a mock model that answers every turn with the model id it was asked for and whether the first
turn's marker word is still in the request. "Context kept" means the turn after the switch went to
the new model **and** still carried the earlier turns.

| Agent | Result | How it switches, and how it confirms | Context kept |
|---|---|---|---|
| Claude Code 2.1.318 | **Live** | `/model <id>`. Mid-conversation the CLI first asks "Switch model?" (the history is cached for the old model and is re-read); Enter answers "Yes". Then `Set model to <label>` | Yes |
| Codex 0.167.0 | **Live** for ids in its list; **restart with resume** for a custom endpoint | `/model <slug>` → `Model changed to <slug> <effort> for this session only`. An id outside the CLI's own list is refused (`Model '<id>' is unavailable`), and for a custom endpoint that list is the bundled one (`model-discovery.md`), so an endpoint's own ids cannot be switched live | Yes |
| OpenCode 1.18.35 | **Live** only | `/models` opens a picker; typing the model's display name filters it and Enter selects. Confirmed by the footer (`Build · <name>`) | Yes |
| OpenClaw 2026.7.1-2 | **Live** | `/model <provider>/<model>` → `model set to <provider>/<model>`. The choice is stored with OpenClaw's session and survives a restart with resume | Yes |
| Cursor (unpinned) | **Restart with resume** | Its `/model <filter>` only opens a picker (read from the CLI's bundle). The TUI does not start without a real login, so a live switch could not be verified; the start command already passes `--model` with `--resume` | Not verified |

Three observations shaped the design more than the table rows:

- **OpenCode sends `/models <arg>` to the model as a prompt.** Only the bare command opens the
  picker. Sending the argument form would have put the model id into the conversation as a user
  message.
- **A resumed conversation keeps its old model unless the start command overrides it.** Codex's
  `resume <id>` ignored a changed `model` in `config.toml` and answered on the thread's recorded
  model; `resume <id> --model <id>` switched it with the context kept. So the edit dialog's "applies
  on the next restart" (#461) was not true for a resumed Codex session; the driver now passes the
  model on a resume as well. OpenCode's `--continue` kept the session's last model even with both
  the config `model` and `--model` set, which is why OpenCode switches live only.
- **Claude Code saves a live switch as the default** in `~/.claude/settings.json`. The session's
  stored model (`ANTHROPIC_MODEL` at start) takes precedence over that file, so the stored model is
  updated after every confirmed switch; otherwise a restart would go back to the old one.

## How the Workspace switches

The session bar shows the model the session runs on (the CLI's default until one was chosen) and,
for the owner and collaborators, opens a picker with the account's list from #461 and a field for
any id. Before anything is pressed it says which of the two ways applies.

**Live** (Claude Code in the terminal, Codex on a provider account, OpenCode, OpenClaw): the hub
sends `POST /agenthub/model` to the session's pod, and the session agent types the CLI's own
command into the same PTY a person types into (`common/model-switch.js`, the plans in each
driver's `modelSwitch`). The answer comes only after the CLI's own confirmation: Claude Code's
`Set model to …` once its "Switch model?" dialog is answered, Codex's `Model changed to <id>`,
OpenClaw's `model set to <id>`, OpenCode's state file naming the picked model. A confirmation
counts only when more such lines are on the screen than before, so switching back to a model used
earlier is not confirmed by the old line. A refusal the CLI prints (Codex's "unavailable") is
reported with its own words; no confirmation within 15 seconds is an error, never a success. Once
confirmed, the hub stores the model as the session's model, so the next start resumes on it —
Claude Code and Codex would otherwise go back to the one the session started on.

**Restart** (Cursor, Codex on a custom endpoint, Claude Code in chat mode, and Claude Code or
Codex while the session is not running): the hub stores the model, and the next start passes it
and resumes the conversation. OpenCode and OpenClaw have no restart path — OpenCode's resume keeps
the conversation's last model, and OpenClaw takes no model at start — so for them a stopped session
is refused with that reason.

**Never during a turn.** The command goes into the input a person and the fleet also type into, so
it waits for the same safe point the fleet's idle delivery waits for (`next-turn.js`): no turn
running, no question or approval open, no CLI menu on the screen, output quiet. It is refused
rather than queued: a switch that lands minutes later, between two turns nobody watched, changes
the model under a conversation the person has moved on in. While it types, the person's keys are
held and go in after it, as they are for a typed fleet message. The Workspace greys out the button
with the same reasons where it already knows them (a working or waiting turn), so the round trip is
the exception.

**A draft in the prompt box.** A typed command would be appended to text a person left in the
prompt and sent as one line. The fleet path counts any key since the last submitted prompt as a
possible draft; for the switch that blocked far too often — answering a question in the Workspace,
moving through a menu, or sending a slash command from the composer (no prompt hook reports those)
all left it blocked until the next message. The session agent therefore tracks the prompt box
itself: printable text outside a question or approval makes a draft; Enter, Ctrl+C and Ctrl+U end
it; arrow keys and Escape do not make one.

**Who.** Owners and collaborators, the same people who may type into the terminal, where the CLI's
own `/model` does the same. Viewers read the current model; the hub refuses their switch (403).

## Rejected alternatives

1. **Restart every switch with resume.** One path for all agents, and the resume is proven
   (#461). But a restart takes the session away for the length of a pod start, kills a running
   shell command and drops the CLI's in-memory state (a half-typed draft, an open background
   job); four of the five CLIs switch in place in under a second. And for OpenCode a restart does
   not switch at all.
2. **Let the person type `/model` in the composer.** Already possible (#461's note). The syntax
   differs per CLI, OpenCode's obvious form sends the id to the model as a prompt, Claude Code's
   dialog needs an answer, and nothing records the choice for the next start: Claude Code and
   Codex silently go back to the start model on the next restart.
3. **Queue the switch until the turn ends.** See above: the person who asked may be gone, and the
   next turn they start could be on a model they did not expect to be on yet.
4. **Set OpenCode's model through its server API.** The TUI's model is TUI-local state; the server
   has no endpoint that changes it (the `/tui/*` routes open the picker but cannot pick), and the
   per-prompt model on `session.prompt` would only apply to prompts the hub sends, not to what a
   person types.
5. **Read the confirmation from the CLI's transcript.** Only Codex records a model change there,
   and only at the next turn; the screen is where each CLI says it at once.

## Limits

- Claude Code and OpenClaw accept any id without checking it against the account; a wrong one
  fails at the next turn, as a typed `/model` would.
- Codex refuses ids outside its own list while it runs, including a custom endpoint's ids; those
  apply at the next restart.
- OpenCode's picker searches by display name. Two models with the same name in the session's list
  cannot be told apart that way, and the switch is refused with that reason; the person can still
  pick in the terminal. A switch that lands on another model than the one picked is reported as such.
- Cursor's live switch is not verified: its TUI does not start without a real login. The restart
  path (`--model` with `--resume`) is the existing start command, also not run with a real login.
- OpenClaw's switch is stored in OpenClaw's session, not in the hub: the hub's model field stays
  empty for OpenClaw (it takes no model at start), so the bar shows the switched model until the
  page is reloaded and the CLI's own status line shows it after that.
- A runtime image older than this change has no `/agenthub/model`; the hub says so and the edit
  dialog's model still applies at the next start.

## Verification

- Pinned CLIs in a PTY (`tests/ModelSwitchProof/cli-switch.mjs`): the table above, the remap of a
  legacy Claude id, Codex's refusal on a custom endpoint, Codex and OpenCode resuming on the old
  model with a changed config, `resume <id> --model` moving Codex, Claude Code resuming on a changed
  `ANTHROPIC_MODEL`, OpenClaw keeping a switch across a resume.
- Real session agent with each pinned CLI (`session-agent-e2e.mjs`, Claude Code, Codex, OpenCode,
  OpenClaw): the switch confirmed through `POST /agenthub/model`, the next turn on the new model with
  the first turn still in it, a refusal during a running turn with nothing typed, an invalid id
  and a wrong token refused.
- Chromium against the real `SessionModelsController` and `SessionModelSwitch` on Postgres, the real
  session agent and the real Codex CLI (`browser-proof.mjs`, 17 checks): the account's list, the
  live switch and its confirmation, the stored model, the next turn, the refusal while working, a
  viewer and a collaborator, 390 px.
- Unit tests: `common-server-model-switch.test.js` (safe point, hooks, dialogs, refusals, drafts),
  `opencode-model-switch.test.js`, the Codex resume command, `ModelSwitchTests` (policy, live and
  restart paths, pod refusals, roles, the Postgres column update), the picker component.
