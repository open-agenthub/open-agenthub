# Sessions created by an API, taken over by a human later

A caller — the REST API, the remote MCP endpoint, or the stdio MCP server — creates a session with
a task, gets a link back, and hands that link to a person. The person opens a terminal on a session
that has already been working. This file records the decisions that made that possible and the
alternatives each one rejected.

## The failure this started from

`AGENTHUB_PROMPT` was only ever passed in the non-interactive branch of each driver. An interactive
session created with a task came up idle and stayed idle until somebody typed, which made
"create a session, hand it over" useless: the person received a session that had done nothing.

The obvious fix — run the prompt through print mode — is wrong. Print mode answers once and exits,
so there would be no live session left to take over. What was needed was the opposite: submit the
prompt *and* keep the REPL.

## How each runtime is given its initial prompt

Every one of the four CLIs can start interactively on a prompt, but no two spell it the same way.

| Runtime | Interactive initial prompt | Verified |
|---|---|---|
| Claude | trailing positional argument | `claude [options] [command] [prompt]` |
| Codex | trailing positional argument | `codex [OPTIONS] [PROMPT]`, help calls it "Optional user prompt to start the session" |
| Cursor | trailing positional argument | `agent [options] [command] [prompt...]`, "Initial prompt for the agent" |
| OpenClaw | `tui --message <text>` | `tui` has no positional; `--message` is "Send an initial message after connecting" |

Two rules hold for all four:

- **Options come before the prompt.** Cursor's positional is variadic (`[prompt...]`), so any flag
  placed after it is swallowed as prompt text instead of parsed.
- **Never on a resume.** A restored conversation already contains the task. Submitting it again
  would make a resumed session start its work from the top, which is worse than doing nothing. A
  resume that was *requested* but has no restored state is a fresh start, so there the prompt does
  apply.

## How each runtime is given the caller's system prompt

`SystemPrompt` on the create request is the caller's standing rules for the session, as opposed to
the task. Every one of the four CLIs offers a way to **replace** its system prompt, and every one of
those is a trap: replacing takes the CLI's own tool, sandbox and environment instructions with it,
so a caller adding one line of persona would get an agent that cannot use its tools. Each runtime
therefore uses its *appending* path, and the replacing one is deliberately left alone.

| Runtime | Appending path used | Replacing path avoided |
|---|---|---|
| Claude | `--append-system-prompt` | `--system-prompt` |
| Codex | `$CODEX_HOME/AGENTS.md`, the global project doc | `model.base_instructions` |
| Cursor | `.cursor/rules/agenthub-session.mdc` with `alwaysApply: true` | — (no CLI option at all) |
| OpenClaw | `<agentDir>/APPEND_SYSTEM.md` | `SYSTEM.md` |

Codex's global `AGENTS.md` is additive and provably so: `codex debug prompt-input` renders both docs
inside one `<INSTRUCTIONS>` block — the global one first, then `--- project-doc ---`, then the
checked-out repository's own `AGENTS.md`. A caller's instructions cannot silently drop the rules the
repository ships.

Codex and OpenClaw take the *global* location rather than a file in the workspace for the same
reason: with a single repository `AGENTHUB_WORKDIR` **is** the clone, so a file there would either
overwrite the repository's own instructions or leave a stray in a tree the agent is about to commit.
Leaving the project-scoped location free also means a repository that ships its own file still wins,
which is the precedence the CLIs intend.

Cursor is the exception, because it has nowhere else. Its CLI has no system-prompt option and its
rule loader is rooted at the workspace — `.cursor/rules/*.mdc`, `AGENTS.md`, `CLAUDE.md` and
`.cursorrules` are all resolved against the workspace root, with no home-directory or environment
equivalent. So the prompt has to be a file in the working directory. `.cursor/rules/` and not
`AGENTS.md`, because a rule file adds to what the repository ships instead of overwriting it, and
`alwaysApply: true` is what makes the loader treat a rule as global rather than glob-matched —
without it the text would only reach the model when Cursor judged it relevant. The file is named in
`.git/info/exclude` so an agent running `git add -A` cannot commit AgentHub's plumbing into the
user's branch; `.gitignore` would have been the wrong lever, since it is tracked and modifying it is
the very problem being avoided.

**Writing is unconditional, including the delete.** Codex and OpenClaw read these files from inside
their state directory, and the state directory is restored from the session's own tar on every
start. If an empty `AGENTHUB_SYSTEM_PROMPT` left the file alone, instructions from an earlier
incarnation of the session would keep applying, which looks like the agent inventing rules nobody
gave it. Writing or removing on every start makes the file say exactly what the request said.

## Folder-trust dialogs, which are what actually blocked this

Three of the four CLIs stop an interactive session on a trust question before running anything.
Print mode skips it, which is why autonomous sessions never hit this and why it only surfaced once
interactive sessions were given work to do. With nobody at the terminal the session sits on a dialog
instead of working, and the person who receives it an hour later finds no progress.

| Runtime | Dialog | Pre-accepted by |
|---|---|---|
| Claude | "Quick safety check: Is this a project you trust?" | `projects.<dir>.hasTrustDialogAccepted` in `~/.claude.json` |
| Codex | "Trust this folder? Codex can read, edit, and run files here" | `[projects."<dir>"] trust_level = "trusted"` in `$CODEX_HOME/config.toml` |
| Cursor | workspace-trust prompt | `--trust`, which the non-interactive branch already passed |
| OpenClaw | none | — |

Nothing is consented to on the user's behalf that they had not already chosen: the directory is the
workspace of a session their own account asked for, holding the repositories they named.

Codex's trust table is **appended** to `config.toml`, not written over it. The entrypoint builds that
file from scratch on every start and the MCP server tables are already in it, so a rewrite would
drop them. Appending also means the table header has to come last, which is why the call sits after
the MCP config is rendered — appending earlier would put `trust_level` inside the last MCP table.
The directory is escaped for a TOML basic string so a path containing a quote or backslash cannot
end the key early.

## The link handed to a person

`SessionInfo.Url` is built from `FrontendOrigin`, not from the request:

- Not from the request host, because the URL is handed to a user and a forwarded `Host` header would
  send them wherever the forwarder claimed to be.
- Not from `Mcp:PublicUrl`, although both name the same deployment. That value is the OAuth issuer
  and MCP resource identifier and only exists when the remote MCP endpoint is switched on, so an
  instance driving sessions over the REST API alone would get no URL at all. `FrontendOrigin` is the
  origin of the web app the link has to open, the chart renders it for every install, and the chat
  notifiers already build `/s/{id}` links from it.

Absent configuration yields `null` rather than a guess.

The stdio MCP server returns sessions through an allowlist, so `url` had to be named there
explicitly — a field the backend starts returning is dropped until it is listed, and without it a
caller would be left holding an id it could not turn into a link.

## Where a runtime's MCP configuration lives

Each CLI reads its own, and all four locations are outside the workspace:

| Runtime | Central location | Written by |
|---|---|---|
| Claude | `mcpServers` in `~/.claude.json` (user scope) | `claude/mcp-config.mjs` |
| Codex | `[mcp_servers.*]` in `$CODEX_HOME/config.toml` | `codex/mcp-config.js` |
| Cursor | `$CURSOR_CONFIG_DIR/mcp.json` | `cursor/mcp-config.js` |
| OpenClaw | `mcp.servers` in `$OPENCLAW_CONFIG_PATH` | `openclaw/mcp-config.js` |

The effective config used to be copied to `$AGENTHUB_WORKDIR/.mcp.json` as well, which was wrong
on both counts. With a single repository the working directory *is* the clone, so it left an
untracked file in a tree the agent is about to commit. And the file did nothing: measured against
Claude Code 2.1.283, a server declared in a project `.mcp.json` reports

```
probe: node -e 0 - ⏸ Pending approval (run `claude` to approve)
```

and is never connected to — exactly what an unattended session cannot provide. The same server in
the user scope is health-checked and connected immediately, from any working directory. Codex and
Cursor ignore `.mcp.json` outright; `cursor-agent mcp list` even names its own locations in the
error it prints when none is configured, and `codex mcp list` reports nothing until a
`config.toml` entry exists. The copy dated from the initial commit, when Claude was the only
runtime, and was carried into the shared entrypoint unexamined when the others were added.

The agent is still launched with `--mcp-config`, unchanged; the user scope is what anything *else*
running `claude` inside the session sees, where no flag is passed. A name present in both is not a
conflict — the CLI gets past configuration parsing to the auth check, while a malformed config
fails before it with "Invalid MCP configuration".

Managed server names are recorded under a private key rather than `mcpServers` being replaced
wholesale: `~/.claude.json` is restored from the session's own state tar, so a server dropped from
the config has to disappear instead of lingering from an earlier start — and a custom image's own
user-scoped servers must survive.

### OpenClaw, which had no MCP wiring at all

Its entrypoint configured nothing and its driver passes no config flag, so a session's MCP servers
simply did not exist for OpenClaw. `openclaw/mcp-config.js` now writes them into `mcp.servers`,
converting the AgentHub document the same way the Codex and Cursor converters do, including dropping
runtime-owned names so a user config cannot shadow a builtin with a server of its own.

Three things about OpenClaw forced decisions the other runtimes did not:

- **It clears the environment for an MCP child.** A probe child saw two variables, with neither
  `RUNTIME` nor `AGENTHUB_CALLBACK_TOKEN` among them — the same behaviour as Codex, and the reason
  every variable a builtin needs has to be stated in its entry.
- **Those variables are written as `${NAME}` references, not values.** OpenClaw interpolates them
  from its own environment (verified: a `${AGENTHUB_CALLBACK_TOKEN}` reference reached the child as
  the real token, a literal passed through unchanged). This matters because `~/.openclaw` is the
  state directory and is archived into the session's state tar and uploaded — a literal token in
  that file would be a credential leaving the pod. Only variables that are actually set are
  referenced, because OpenClaw reports an unresolvable one as `Missing env var "X"` on every command.
- **The managed marker sits on each server entry, not at the config root.** An unknown root key makes
  OpenClaw reject the whole file (`<root>: Invalid input` from `openclaw config validate`), while an
  unknown key inside a server entry validates cleanly. The bookkeeping is needed for the same reason
  as Claude's: the config returns from the state tar, so a withdrawn server has to disappear while a
  server the agent added itself with `openclaw mcp add` has to survive.

Only `agenthub_files` and `agenthub_network` are rendered, because those are the only builtins the
OpenClaw image ships — it carries neither `browser/` nor `sessions/`.

### A builtin that is enabled but not in the image

The enabling flags (`AGENTHUB_BROWSER_ENABLED`, `AGENTHUB_SPAWN_MCP_ENABLED`, …) are instance-wide
and not gated per agent, while the images do not all ship all builtins. The shared entrypoint ran
`node "$RUNTIME/<builtin>/configure.mjs"` unconditionally, which for OpenClaw meant a missing module:
`node` exited `MODULE_NOT_FOUND`, and under `set -e` in a sourced script that killed the entrypoint
before the agent ever started. On an instance with the browser or the spawn MCP switched on, an
OpenClaw session could therefore not start at all.

`merge_builtin_mcp` now checks the module exists and logs a skip instead. Reproduced before the fix
and confirmed after it: with both flags on and an OpenClaw image layout, the entrypoint runs to the
end and configures the two builtins it does have.

Whether the OpenClaw image *should* ship `sessions/` (orchestration) or `browser/` is a separate
decision — `browser/` additionally depends on the browser-runtime sidecar — and is deliberately not
settled here.

## Following a session without a websocket

`GET /api/remote/sessions/{id}/transcript` and the `session_transcript` MCP tool page the transcript
with an offset cursor. Re-fetching from zero on every poll would mean downloading a transcript that
grows into the megabytes to read the few lines that changed. The text is the provider's own
conversation rendered as role sections where the runtime records one, the cleaned terminal
scrollback otherwise — `docs/transcripts.md` says why and what that means for offsets.

Two decisions inside it are worth keeping in mind:

- **Phase is read before the text.** A session that finishes between the two reads is then reported
  as still running with its final output already included, so the caller polls once more and sees
  the terminal phase. The other order would report "finished" with output still missing, and a
  caller that acted on it would have acted on a truncated transcript.
- **`length` is always reported, and an out-of-range offset is clamped rather than rejected.** The
  session agent keeps only the last 1 MB of scrollback and re-uploads that whole window, so a
  session that talks past 1 MB loses text from the front and every offset behind it shifts. There is
  no cheap way to detect that; a poller that sees `length` go down knows its cursor no longer means
  what it did. Clamping keeps such a poller producing empty pages instead of 400s until it catches
  up.

`running` is reported alongside the text rather than left to the caller to infer from the phase
string, so a poller cannot loop forever on a phase name it does not recognise.

## How these claims were verified

Against the pinned CLI versions, not from memory: Claude Code 2.1.283, Codex 0.157.1, Cursor Agent
2026.09.28, OpenClaw 2026.7.1-2.

- **Codex, end to end.** The module runs against the real CLI with a reproduced `config.toml`:
  `codex debug prompt-input` shows both the caller's prompt and the repository's own `AGENTS.md`, the
  MCP table survives the appended trust table, and an empty prompt removes the doc.
- **Claude.** `--append-system-prompt`, `--system-prompt` and the positional prompt confirmed from
  the CLI's own help.
- **Cursor and OpenClaw.** Confirmed from each CLI's shipped bundle — Cursor's rule loader and its
  `alwaysApply` handling, OpenClaw's `discoverAppendSystemPromptFile` and the
  `base + "\n\n" + append` assembly. Neither CLI offers an offline way to dump its assembled prompt,
  so these two are verified at source level rather than by observing a live session. Worth
  confirming against a running instance before relying on them.
