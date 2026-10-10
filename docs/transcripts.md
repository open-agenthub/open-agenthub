# Transcripts: what a session "said", and where that comes from

A session's conversation is read in four places: the Transcript tab of the web app, a shared link,
the token-authenticated remote API and the MCP tools (`session_logs`, `session_transcript`). The
chat relays also quote from it. This file records how that text is sourced and why; the three
design notes under `docs/superpowers/specs/` (`2026-07-27-clean-transcripts-design.md`,
`2026-08-01-conversation-transcripts-design.md`, `2026-08-02-transcript-noise-compaction-design.md`)
describe the presentation layer they feed and still apply.

## Raw and clean are two different things

The session agent (`agent-runtime/common/server.js`) keeps the last 1 MB of what the agent's PTY
wrote — control sequences, cursor moves, redraws — and uploads that window every 30 seconds to S3
and to the hub (`PUT /internal/sessions/{id}/scrollback`). That is the *raw* scrollback. People
never see it: the user-facing endpoints strip terminal control sequences at the boundary
(`AgentTerminal.CleanTranscript`), which is the decision in the 2026-07-27 note.

The resume path is not a person. A resumed session runs in a fresh pod with an empty buffer, so the
agent asks the hub for its history (`GET /internal/sessions/{id}/scrollback`) and seeds its buffer
with the answer — and from then on persists that seed as part of its own scrollback. The first
implementation answered that request with the *cleaned* text. Every resume therefore replaced the
raw history with stripped text, and the terminal replay of a session resumed twice was a staircase
of half-drawn screens with no colours. The internal endpoint now returns the raw copy
(`ISessionService.GetScrollbackAsync`, `TranscriptReader.ReadRawAsync`); stripping happens only in
`GetTranscriptAsync`, which the user-facing controllers call.

**Alternative considered:** strip at upload time and store only clean text, which would make the
two paths identical. Rejected because the 2026-07-27 note already decided to keep the raw bytes for
terminal replay, and because stripping is lossy in ways we cannot undo later — a better renderer
(or a terminal emulator replay) needs the original bytes.

## One cap

Three places bounded the scrollback, with two different numbers: the agent keeps 1 MB, the Postgres
copy was cut to 400 KB (behind a comment claiming it "matches the agent's buffer"), and the paging
record allowed 1 MB. On an instance without S3 a resume is seeded from Postgres, so it came back
600 KB shorter than what the pod had uploaded, and a poller that trusted `TranscriptPage.MaxChars`
could ask for more than the store would ever hold.

`ScrollbackLimits.MaxChars` (1 MB) is now the one constant: the upload endpoint trims to it and
`TranscriptPage.MaxChars` is defined as it. The agent cannot reference a C# constant; `MAX_BUFFER`
in `server.js` carries a comment naming `ScrollbackLimits`, and `ScrollbackRoundTripTests` pins the
value so a change on either side has to be made knowingly on both.

1 MB rather than 400 KB because the agent's window is the natural upper bound — the hub can never
receive more than the pod holds — and because a Postgres `TEXT` of 1 MB per session is not a
cost worth losing history over. 400 KB had no recorded rationale.

## The native transcript is the source; the scrollback is the fallback

Until now nothing read the files the agent CLIs write for themselves. "Transcript" meant the PTY
scrollback with control sequences stripped, and the Transcript tab guessed at structure with
heuristics (`frontend/src/lib/transcript.js`: spinner glyphs, short-burst suppression, adjacent
redraw dedup). The 2026-08-02 note already said the better long-term source would be semantic
events from the runtime and deferred it. This is that source — not events, but the file the
runtime keeps anyway.

**What is uploaded.** The session agent asks its driver for the provider's own conversation file
(`driver.findTranscript`) on every 30-second persistence tick until one exists, then uploads it
next to the scrollback whenever it changed: the whole file to S3 (`transcript.jsonl`, presigned
`AGENTHUB_TRANSCRIPT_PUT_URL`) and the tail — capped at the same `ScrollbackLimits.MaxChars`, cut
at a line boundary so the first kept line is a whole record — to `PUT /internal/sessions/{id}/transcript`
for the Postgres copy. Unchanged files are skipped by size and mtime; an idle session must not
re-upload megabytes every half minute.

- **Claude Code** names the file from two things we fix ourselves: the session id we pass with
  `--session-id`/`--resume`, and the working directory the PTY starts in, slugged by replacing
  every character outside `[A-Za-z0-9]` with a dash (`docs/session-transfer.md` relies on the same
  rule). The path is known before the file exists — it appears with the first turn — so the driver
  simply checks for it. The chat UI mode (`-p --output-format stream-json`) writes the same file.
- **Codex** picks the thread id itself after starting and writes
  `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-<timestamp>-<id>.jsonl` with a `session_meta` first
  line. The driver scans for rollouts touched since the agent was launched, reads the id from
  `session_meta` and records it in `$CODEX_HOME/agenthub-thread-id` — inside the state directory,
  so the archive a pause uploads carries it to the next resume.
- **Cursor, OpenClaw and OpenCode** have no `findTranscript`; they keep the scrollback fallback.
  OpenCode keeps its conversation in its own database under `~/.opencode`, not in a JSONL file
  the reader could serve.

**Codex resumes by id, not `--last`.** `codex resume --last` means "newest rollout in this
directory wins". A `codex` someone ran from the shell tab, or a thread the TUI opened with `/new`,
becomes the session's conversation on the next resume and the real one is silently left behind —
and because the driver's missing-resume check only fires on an error, nothing would ever say so.
With the id recorded, the resume command is `codex resume <id>` (and `codex exec resume <id>` for
unattended modes), which the pinned CLI documents alongside `--last`. `--last` stays as the
fallback for archives from before the id was recorded. The alternative of storing the thread id in
the hub's session record was rejected: it would need a new callback and a column for a value that
only the pod ever uses, and the state archive already travels exactly where the id has to go.

This could not be verified in a container here (no Codex CLI on the build machine); the discovery
and the resume arguments are unit-tested against the documented rollout layout, and the first
resume on a cluster should be watched for `Transcript: …` in the pod log.

**How it is served.** `SessionTranscripts` is the one place that decides what a reader gets:
`ISessionService.GetConversationAsync` parses the native file (S3, then Postgres) into
`TranscriptEntry` rows — `user`, `assistant`, `tool` (with the tool name), `result` — and every
surface falls back to the cleaned scrollback when there are none.

- The web app's Transcript tab and shared links read `GET …/conversation`, a `ConversationPage`
  whose `source` says which shape it carries. For `native` the page holds entries and the offsets
  count entries; for `scrollback` it holds text and the offsets count characters, exactly like the
  remote `TranscriptPage`. The frontend treats `nextOffset` as opaque and renders roles only for
  `native`; the heuristics in `lib/transcript.js` now run only on the fallback.
- The remote API and the MCP tools return text, so the entries are rendered as `## User` /
  `## Assistant` / `## Tool: Bash` / `## Result` sections. The rendering is append-only as the
  conversation grows (a test pins this), which is what keeps a character offset valid across polls.
- A chat session's scrollback *is* stream-json in the same line shape as the native file, so a chat
  session from before this change is parsed through the same reader instead of handing the remote
  API raw JSON. The terminal replay (`GET …/transcript`) is unchanged: the ended terminal pane and
  the chat pane need the scrollback as it was.

**What the reader skips, and why.** Claude's `isMeta` lines (command caveats, injected tool
guidance) and `isSidechain` turns (sub-agents the main conversation already summarises); Codex's
`event_msg` lines, which repeat `response_item` lines, and user messages that consist of a single
XML element (`<user_instructions>`, `<environment_context>` — the CLI injecting AGENTS.md, which
would otherwise open every transcript). Thinking and reasoning blocks. Tool arguments and results
are clipped to 2 000 characters: the point of the transcript is what was said and done, not a
second copy of every file the agent read.

**Alternatives considered.** Parsing in the session agent and uploading entries instead of the
raw file was rejected because the raw file is also the resume state, so uploading it whole costs
nothing extra and keeps the parser in one language with one test suite. Replaying the raw ANSI
through a terminal emulator (the 2026-08-02 note's other option) recovers a final screen, not a
conversation. A separate `ISessionStore` interface member for every fake in the test suite was
avoided by giving the new store and service members default implementations; only the real
implementations override them.

## The Transcript tab renders prose as markdown, everything else verbatim

A native page's `user` and `assistant` entries are what the person and the model wrote, and
both write markdown: headings, lists, fenced code, tables, the occasional mermaid diagram. The
tab renders those two roles through the same escape-first renderer the chat pane uses
(`lib/markdown.js`, diagrams via `lib/mermaid.js`), so a turn looks the same whether it is read
live in the chat or later in the transcript. The styles for rendered markdown live in
`style.css` for that reason — they used to be scoped to the chat pane, and a second copy would
have drifted.

Tool calls, tool results and the scrollback fallback stay in a `<pre>`. A diff, a shell listing
or a stack trace contains `*`, `_`, `#` and `|` in positions that are not markup, and the
renderer would turn an honest `git diff` into stray emphasis and broken tables. The renderer is
escape-first, so this is a presentation choice, not a safety one: agent text cannot inject
markup either way.

## The Transcript tab follows a running session

The tab used to load once when opened and never again while the session ran; a person reading
it saw the state of the first click. It now polls `…/conversation` every four seconds from the
cursor of the last page while the tab is active and the session is live, appends what comes back
(`mergeConversationPage`), and fetches once more when the phase settles — the session agent
uploads a last time as it exits, and that tail would otherwise be missing until the next visit.

Polling rather than the session event socket: the transcript is appended by a 30-second upload,
so there is no finer-grained event to push, and a cursor poll costs the hub one small page — a
push would still need the same read to find out what is new. Four seconds is the interval the
permission and inbox polls in the same view already use, so the tab adds no new timer.

A page that cannot be appended is not appended: `length` going down, the server clamping the
offset, or the source switching from `scrollback` to `native` (the first upload landing after the
tab opened) all mean the cursor no longer points into the history the tab holds, and the tab
reloads from the start. Stitching the two would show a conversation that never happened.

## The notification hook filters by type, not by the word "permission"

`agent-runtime/claude/hooks/notify-hook.sh` is the one place a real JSONL transcript was already
read: it replaces the CLI's generic "waiting for your input" with the last assistant text from
`transcript_path`, so the chat relays quote the actual question. It dropped notifications whose
message matched `/permission/i`, meant to keep the permission prompt (handled by the PreToolUse
hook) out of the messengers. The regex also swallowed every genuine question that contained the
word — "do I have permission to force-push?" never reached anyone.

The pinned CLI sends `notification_type` with every notification (verified in the 2.1.28x binary:
`permission_prompt`, `idle_prompt`, `auth_success`, `elicitation_dialog`, …). The hook now drops
`permission_prompt` and `auth_success` by type and forwards everything else; a payload without the
field (an older CLI) is matched against the one fixed phrase `Claude needs your permission`, not a
substring. The extraction itself is now under test with a fixture transcript: the last assistant
line that has *text* wins, a trailing tool call or a half-written line does not.

## Smaller things fixed on the way

- **A fast crash no longer forfeits the resume.** The Claude driver treated any non-zero exit
  within ten seconds of a `--resume` launch as "no saved conversation" and relaunched fresh,
  dropping `--resume` for good. An expired login and an unreachable API are the two fastest ways
  for a resume to die, and both used to cost the history. Only the CLI's own "No conversation
  found" counts now; the Codex, Cursor and OpenClaw drivers already matched their CLIs' words.
- **The ended-terminal replay sets `convertEol`.** The saved transcript comes back with control
  sequences and carriage returns stripped, so its lines end in a bare `\n`, which xterm renders
  as a staircase. The option is set only for the replay: a live PTY sends its own `\r\n`, and
  converting there would alter raw-mode output.
- **Dead code removed:** `AgentTerminal.ReadScrollbackAsync` (a websocket scrape of the pod
  nothing called since transcripts moved to storage), `SlackThread.PostedLen` and
  `SetPostedLenAsync` (from a design that streamed transcript into the thread; the column stays
  in the table at its default), and the `model`/`lastResult` bookkeeping in the chat log that no
  component displayed.

## Compatibility

Nothing changes for sessions created before this: no `transcript.jsonl` exists,
`GetConversationAsync` returns null, and every surface serves what it served before. A runtime
image older than the hub ignores `AGENTHUB_TRANSCRIPT_PUT_URL`; a hub older than the image answers
the new `PUT …/transcript` with 404, which the agent treats like any other failed best-effort
upload.
