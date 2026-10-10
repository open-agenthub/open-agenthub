# How a session reaches Slack, Telegram and Signal

## The one event that matters

All three relays open a session's thread (Slack) or binding (Telegram, Signal) on exactly one
occasion: a `"question"` event. `"finished"` and `"failed"` are posted *into* an existing
thread and dropped when there is none; a reply from chat is routed by thread, so without one
it finds no session. The backend is provider-neutral about this — `InternalController.Notify`
fans the event out for any session that authenticates with its callback token.

The event itself, however, comes from one place only: an agent hook inside the session pod
posting to `/internal/sessions/{id}/notify`. Until 0.12.0 the only such hook was Claude's
`Notification` hook. Codex, Cursor and OpenClaw sessions therefore never appeared in chat at
all — not even their "finished", because no thread existed to post it into. The design intent
("Codex uses lifecycle events available from its runtime-owned hooks to report that an
interactive turn stopped") was written down in the Codex design spec and never built.

## What fires, per runtime

| Runtime | Hook | Moment | Message |
|---|---|---|---|
| Claude | `Notification` (`claude/hooks/notify-hook.sh`) | the CLI's own "waiting for input" notification, after idle | last assistant text from the transcript |
| Codex | managed `Stop` (`codex/turn-notify-hook.js` → `common/turn-notify-hook.mjs`) | end of every turn | `last_assistant_message`, else the rollout's last assistant text |
| Cursor | `hooks.json` `stop` (`common/turn-notify-hook.mjs`) | end of every turn | generic — the stop payload carries no text |
| OpenClaw | none | — | **not relayed** |
| OpenCode | `event` hook of the managed policy plugin (`opencode/turn-notify.mjs`) | `session.idle` of a top-level session | the turn's assistant text, read through the plugin's own client |

OpenClaw has no per-turn hook of any kind (its approvals cannot reach the relay either, see
`CLAUDE.md`). An OpenClaw session is reachable through the web terminal only; the README's
chat section says so rather than promising otherwise.

### OpenCode: an event, not a hook command

OpenCode runs no hook commands. Its plugins receive the server's events, and `session.idle`
fires once a session has finished its turn, so the relay lives in the same plugin that gates
tool calls — registered in the read-only managed config, where the agent cannot drop it. The
plugin's own SDK client reads the session's messages for the text; the alternative, parsing
OpenCode's storage from disk, would tie the relay to a layout that is not an interface.

Two idles are not the user's turn ending and are skipped: a session with a `parentID` is a
subagent the `task` tool started (its idle is a step inside the parent's turn), and a last
message that ended in `MessageAbortedError` was interrupted from the keyboard.

Verified against the pinned 1.18.35 with a mock model and a mock hub: an interactive `run`
posts one question with the final text; a turn that ran a `task` subagent posts one question,
for the parent, not two; `autonomous` posts none. The TUI could not be driven here (it waits
for terminal query answers); it runs the same server and plugin, but the TUI path is unverified.

### Why Stop, and why one turn early

Codex has no "idle" notification; the end of a turn is the only lifecycle moment a managed
hook sees, so it is the moment the relay learns about. The skill reminder runs on the same
Stop and may continue the turn once; that continuation ends with `stop_hook_active` (Codex)
or `loop_count > 0` (Cursor) and is ignored. The chat therefore sees the substantive answer a
few seconds *before* the agent is truly idle, instead of the reminder's one-line reply. The
alternative — relay only the continuation's end — would deliver "No, nothing reusable." as
the question. A reply from chat takes longer than the reminder turn, so the window is
theoretical.

### Interactive only

In exec/autonomous/scheduled mode the end of the turn is the end of the process; the
session-agent then posts `Succeeded`, which the relays carry as "finished". A "question" there
would open a thread for an answer nobody can give — and `AgentTerminal.SendInputAsync` would
type the reply into a process that has already exited. The hook reads `AGENTHUB_MODE` and
stays silent unless it is `interactive` (unset counts as interactive, as it does for the
driver and the policy hook).

### The title-generation thread

After the first answer the Codex TUI generates a task title in a sub-session of its own: a new
`session_id`, `transcript_path: null`, `permission_mode: "bypassPermissions"`, and a Stop
event of its own carrying the title as `last_assistant_message`. Relayed, that is a second
"question" in chat reading "Answer the branch question". The hook treats a Codex Stop
without a `transcript_path` as such a helper thread. A missing rollout *file* behind a given
path is a different case and still counts as the user's turn.

## What was verified, and how

Against the pinned CLI (`CODEX_VERSION` in `codex/Dockerfile`, 0.160.0 at the time) in the
runtime image, driven against a fake `/v1/responses` endpoint the way
`fixtures/codex-policy-hook-smoke.js` does:

- **Stop payload, exec mode**: `{session_id, turn_id, transcript_path, cwd,
  hook_event_name: "Stop", model, permission_mode, stop_hook_active, last_assistant_message}`.
  Two `[[hooks.Stop.hooks]]` entries under `managed_dir` both run; the hook process inherits
  `AGENTHUB_*` from the CLI's environment.
- **Stop payload, TUI**: same fields for the user's thread, plus the title-generation thread
  described above. Verified with the TUI under `node-pty` inside the image.
- **The rollout file** (`$CODEX_HOME/sessions/…/rollout-*.jsonl`) carries assistant text as
  `{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":…}]}}`;
  that is the fallback the hook reads when `last_assistant_message` is absent.
- **`AgentTerminal.SendInputAsync` reaches the Codex TUI.** The pattern (text, 300 ms pause,
  `\r` as its own write) submitted the prompt: the model request arrived ~370 ms after the
  text was written and contained it verbatim. The pause exists for Claude's paste detection;
  Codex's TUI accepts the same sequence, so one implementation serves both.
- **`fixtures/codex-turn-notify-smoke.js`** pins the exec-mode half of this
  (interactive posts once with the answer and the token; autonomous posts nothing) and runs
  from `codex-container-smoke.ps1`, so a CLI bump that renames `last_assistant_message`,
  stops delivering Stop to managed hooks, or stops passing the environment through fails the
  nightly job rather than going quiet in production.

Not verified: Cursor's stop hook end to end. Its CLI is unpinned and needs a login; the hook's
Cursor branch is covered by the payload shape in Cursor's hook documentation
(`conversation_id`, `generation_id`, `status`, `loop_count`, nullable `transcript_path`) and
by unit tests only.
