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
