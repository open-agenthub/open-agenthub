# Continuing an autonomous session interactively

An autonomous session runs one task and ends. Often that is where the interesting part starts:
the run produced something half right, and the person who reads the transcript wants to tell
the agent what to change — in the same conversation, with the tool calls and file reads the
agent already made still in its context. Before this, the only options were to resume the
session (which runs the same autonomous command again) or to start a new interactive session
and paste the transcript into it.

## The alternative that was rejected

"New interactive session, old transcript as the prompt" looks like the cheap version and was
the first thing considered. It was rejected because it is lossy in a way the person cannot see:
the transcript is prose, and what the agent carried through the run was its *context* — the
tool results it read, the files it opened, the decisions it made along the way. A fresh session
handed the transcript reads a summary of its own work and starts over, usually by re-reading
everything. The agent CLIs already know how to continue a conversation losslessly, through the
same resume path a paused session uses, and that path is the only one that keeps the context.

So a conversion is not a copy. It is an edit of the session record followed by the ordinary
resume, and nothing in the pod is new: `AGENTHUB_MODE` arrives as `interactive`,
`AGENTHUB_RESUME=1` with the restored state, and every driver already branches on exactly those
two values.

## What a conversion does

`POST /api/sessions/{id}/convert` with `{mode:"interactive", uiMode?, autoApprove?, resume?}`
(the same body on `/api/remote/sessions/{id}/convert` and as the `session_convert` MCP tool):

1. **Only `Autonomous → Interactive`, and only while the session can be resumed** (Succeeded,
   Failed or Paused). Anything else is a `409` with `{error}`. A running pod is refused rather
   than stopped: it is still working on the autonomous run, and converting underneath it would
   fork the conversation the same way the state-transfer doc describes for a live pod. Pause
   first. A scheduled session has no single conversation to continue — each CronJob run is its
   own — and an interactive one is already what it is asked to become. The direction is one-way:
   an interactive session has no task to run unattended.
2. **`uiMode`** is validated with the create rule (`chat` is interactive Claude only; `400`
   otherwise), so a converted Claude run can come back in the chat pane.
3. **`autoApprove` defaults to off**, the interactive default. The autonomous run had it on
   because nobody was there to answer a prompt; now somebody is, and an agent that keeps running
   everything without asking is not what "interactive" promises. `autoApprove: true` keeps it —
   the web card offers that as a checkbox.
4. The record remembers where it came from (`converted_from`, surfaced as `convertedFrom`), so
   the session header can say "converted from autonomous" and a reader of the transcript knows
   why its first turns look unattended. That is the only reason for the column; it is not used
   to convert back.
5. **`resume` defaults to true** and calls the normal resume. `resume: false` only flips the
   record — for a caller that wants to change the mode now and start the session later. If the
   resume step fails (agent no longer allowed, usage limit), the record is already interactive
   and the session shows the ordinary Resume button; nothing is half-converted.

`SessionInfo.canConvertToInteractive` carries the first rule to the UI and the MCP clients, next
to `canResume`, from the same `SessionStatus` predicate so the two cannot disagree.

## Which runtimes keep the conversation

| Runtime | After conversion | Why |
|---|---|---|
| Claude | **same conversation** (terminal or chat) | The hub fixes the session id: the autonomous run is `claude -p <task> --session-id <id>`, the interactive resume `claude --resume <id>`. Same file under `~/.claude/projects/`, same id, no prompt re-submitted. |
| Codex | **same conversation** | The driver records the thread id in `$CODEX_HOME/agenthub-thread-id`, inside the state archive. `codex exec … resume <id>` and the TUI's `codex resume <id>` name the same thread. |
| Cursor | new conversation, same workspace | Cursor names its chats itself and the hub never learns the name. The id the hub passes to `--resume` is its own, so the CLI reports the chat as missing and the driver falls back to a fresh start — the existing resume behaviour, not something conversion adds. |
| OpenClaw | new conversation, same workspace | Same: `--session`/`--session-id` are only honoured for an id the CLI itself issued. |

Reading the last-used chat id out of Cursor's or OpenClaw's state directory was considered and
deliberately not done. Neither CLI documents its on-disk session store, and this repository has
not verified the layout against the pinned versions. A guessed path either resumes the wrong
conversation silently — a shell-tab `agent` the person ran in the same pod — or fails on every
start and takes the fallback anyway. The honest version is to say so: the web card's hint line
is per agent ("keeps the conversation" / "starts a new conversation in the same workspace"), and
the README table says the same. If a verified reader for either store is added later, it belongs
in the driver's `buildCommand`, where Codex's `rememberedThreadId` already lives.

**The one case Claude and Codex lose, too:** a session that failed before its first state upload
has no archive. The resume then takes the fresh-start branch, in which the drivers *do* pass the
prompt again, so the task starts over interactively rather than nothing happening at all. The
card cannot know in advance whether an archive exists; the terminal shows which branch ran.

## Two runtime corrections conversion needed

Both come from the same buried assumption: "interactive means subscription login". It was true
while the only interactive sessions were ones people created by hand, and conversion is the
first thing that produces an interactive session with `AuthMode = ApiKey`.

- `BuildPodContextAsync` resolved whether an API key was stored only for Autonomous and Scheduled
  sessions. The pod spec was never affected — it projects the key from the secret in every mode —
  but the context said "no API key" for a session that had one, and the credential preflight is
  the kind of code that reads that flag. It now resolves the key for every `ApiKey` session.
- The Codex driver's `prepare` handed `CODEX_API_KEY` to the agent child only outside interactive
  mode. In the shipped entrypoint this changes nothing: for an interactive API-key session the
  entrypoint runs `codex login --with-api-key` and unsets the variable before the driver sees it.
  What it removes is a second copy of the assumption, in a file that is the first thing a custom
  entrypoint would skip — the key would then sit one process up while the TUI waited on a login
  screen nobody could complete. The driver now passes the key through whenever it is set.

The prompt is **not** re-submitted on a converted resume. Each driver already leaves the initial
prompt out when restored state is present, and a test pins the two-step sequence (autonomous
command, then interactive resume on the same id with no prompt) so that rule survives the next
edit to the interactive branch.

## Where it is exposed

- Web app: an inline card on a finished or paused autonomous session ("This autonomous run has
  finished. Continue it interactively?") with *Open terminal*, *Open chat* (Claude only) and a
  *keep auto-approve* checkbox; the same card unfolds under a row of the sessions list. No
  dialog, per the frontend rules.
- `POST /api/sessions/{id}/convert` and `POST /api/remote/sessions/{id}/convert`.
- `session_convert {sessionId, uiMode?, autoApprove?, resume?}` on the remote MCP server, the
  stdio server and the in-pod server. In the pod it is limited to descendants of the calling
  session, like `session_get` and `session_wait`: an orchestrator may hand one of its finished
  children to a person, not a stranger's session.
