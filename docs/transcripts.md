# Transcripts: what a session "said", and where that comes from

A session's conversation is read in four places: the Workspace of the web app, a shared link,
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

- The web app's Workspace and shared links read `GET …/conversation`, a `ConversationPage`
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
  Claude's streaming Workspace need the scrollback as it was.

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

## The Workspace renders prose as markdown, everything else verbatim

A native page's `user` and `assistant` entries are what the person and the model wrote, and
both write markdown: headings, lists, fenced code, tables, the occasional mermaid diagram. The
saved conversation renders those two roles through the same escape-first renderer the streaming
Workspace uses (`lib/markdown.js`, diagrams via `lib/mermaid.js`), so a turn looks the same
whichever transport delivered it. (This was the Transcript tab's rule; the Workspace timeline
rendered only the model's turns as markdown and took the person's over when the tab went, #113.) The styles for rendered markdown live in
`style.css` under `.md` for that reason — they used to be scoped to the chat pane, and the
workspace branch had grown a second global copy (`.workspace-markdown`) that was dropped when it
was merged, before the two could drift.

Tool calls, tool results and the scrollback fallback stay in a `<pre>`. A diff, a shell listing
or a stack trace contains `*`, `_`, `#` and `|` in positions that are not markup, and the
renderer would turn an honest `git diff` into stray emphasis and broken tables. The renderer is
escape-first, so this is a presentation choice, not a safety one: agent text cannot inject
markup either way.

## The Workspace follows a running session

The Transcript tab used to load once when opened and never again while the session ran; a person
reading it saw the state of the first click. The Workspace polls `…/conversation` every second
from the cursor of the last page while it is shown, the browser tab is visible and the session is live,
appends what comes back (`mergeConversationPage`), and fetches once more when the phase settles —
the session agent uploads a last time as it exits, and that tail would otherwise be missing until
the next visit.

**A running session is read from its pod (#111).** Following the uploaded copy put the Workspace
up to 30 seconds behind the terminal, which is how often the pod uploads. For a running session
the hub now asks the session agent for the transcript itself: `GET /agenthub/transcript?from=<byte>`
(callback token) answers the whole lines appended since that offset, with the file size and the
next offset. `LiveTranscriptReader` keeps, per session, the offset and the entries parsed so far,
so a poll moves only what the CLI wrote since the last one; a file shorter than the cursor or a
new pod starts that session over from zero, and a runtime without the route (404) is not asked
again for five minutes and gets the stored copy. Measured with Claude Code 2.1.287 against a mock
Messages API, through this branch's `server.js`: a reply reached the hub 0.15–0.24 s after the
model finished it, against 8–20 s on the upload path; the browser's one-second poll adds at most
a second.

Rejected: pushing appended lines over the agent WebSocket. In terminal mode that socket carries
raw PTY bytes, so the lines would need a second framing, and the browser would need its own
parser for every provider's format when the hub already has one. Also rejected: uploading on
every change instead of every 30 seconds — the pod would PUT the whole file to S3 and a megabyte
to Postgres several times per turn, for every session, whether or not anyone is looking. The live
read costs nothing while nobody is.

## A resumed pod catches its transcript up from the hub (#146)

After a restart the Workspace briefly showed the previous run and then dropped its last turns.
The hub showed its stored copy while the pod came up, then read the new pod's file live. That
file comes from the state archive, which is written at most every two minutes
(`docs/state-archive.md`) and is missing the end of a pod that died without its SIGTERM. The
transcript, by contrast, reaches the hub every 30 seconds. So the live file was shorter, and
`mergeConversationPage` reloaded from it. The pod's next upload then replaced the hub's copy
with the shorter file, so those turns were gone for good. The resumed agent never had them
either. Reproduced with Claude Code 2.1.287 in this repo's `server.js`, killed without SIGTERM
after the archive had turn 1 and the hub turns 1 and 2:

- The pod's live route served turn 1 only.
- The resumed model was sent turns 1 and 3.
- The upload after the restart held turns 1 and 3.
- Replaying the hub's real `SessionTranscripts.PageAsync` pages through the Workspace merge gave
  *U1 U2 A2* while the pod was down and *RELOAD → U1* once the agent was up.

With the change, the live route served turns 1 and 2 from its first answer, the model was sent
turns 1, 2 and 3, and the view went on from *U1 U2 A2* to *U1 U2 A2 U3 A3* without a reload.

**What the pod does.** On a resume, after the scrollback seed and before the agent starts, the
session agent asks `GET /internal/sessions/{id}/transcript` for the stored file: the S3 copy
joined with the newer Postgres tail, exactly as uploaded. If that copy is longer than the restored
file and starts with it, it is the same append-only file further on, and the agent appends the
difference. The comparison is made against the restored file masked the way uploads are, because
the hub only ever received the masked text. The turns appended from it therefore reach the agent
with secrets masked. A copy that does not start with the restored file is a different history.
That also covers a hub that does not answer within 15 seconds. In both cases the archive is
resumed as it is, which is the old behaviour. Until this has settled, the transcript route
answers "no file" and nothing is uploaded, so neither the hub's live read nor the 30-second
tick can see the older file.

**Rejected:**

- **Merging stored and live in the hub**, which would be "keep the previous run's entries and add
  the live ones". The agent would still resume without the missing turns. Its first upload would
  still overwrite them, so the hub would need a separate per-run history to merge from. The view
  would also disagree with what the agent knows.
- **Archiving the state on every tick.** That is the cost `docs/state-archive.md` removed, and it
  still loses what a pod writes between its last tick and its death.
- **Replacing the restored file with the hub's copy.** On an instance whose S3 copy is stale, the
  hub has only the capped Postgres tail. When the copies disagree, the agent's own state is the
  one its other files (todos, file history) were written alongside.

**Not covered:** a resume that restored no state archive at all, where the CLI starts a fresh
conversation. It writes a new file, and its upload replaces the stored one as before.

## Upload urls expire; the copies are read freshest first (#111)

The presigned urls a pod gets in its environment are signed for twelve hours. A session that ran
longer kept uploading to S3 and got a 403 every time, which nothing reported, while its callback
to the hub kept updating the Postgres copies. Both readers preferred S3 whenever it was non-empty,
so every session older than twelve hours showed a conversation frozen at that mark — the "does not
update at all, not even after 30 seconds" in #111 — and its state archive froze with it, so a
resume after that restored twelve-hour-old state. Reproduced against a real S3 server
with the backend's own `S3ArtifactStore`: after the url expired, Postgres held three
entries and the hub served one.

Two changes, because pods already running cannot be changed:

- **The session agent re-mints its urls** every six hours through `POST /internal/sessions/{id}/artifact-urls`,
  half the signing period, so one failed refresh leaves time for the next. Only an http(s) url the
  hub returned replaces one. Because these urls now arrive over the network, none of them reaches
  a shell any more: the state archive is built by the shell script and uploaded by `curl` run
  with an argument list, as the scrollback and transcript are.
- **The hub reads both copies** (`TranscriptReader.ReadTranscriptAsync` / `ReadScrollbackAsync`).
  The pod writes Postgres after S3 on every tick, so a Postgres copy that S3 does not contain is the
  newer one. For the scrollback both are the same window and Postgres is taken; for the transcript
  Postgres has only the capped tail, so the history before it is kept from S3, joined at the tail's
  first line. This is what makes sessions on older runtime images follow again without a restart.

Rejected: comparing timestamps. Neither copy records when it was written in a way the other can be
compared with — the Postgres row's `updated_at` moves for every status change — and the content
check needs no new column.

A page that cannot be appended is not appended: `length` going down, the server clamping the
offset, or the source switching from `scrollback` to `native` (the first upload landing after the
view opened) all mean the cursor no longer points into the history the view holds, and it
reloads from the start. Stitching the two would show a conversation that never happened.

## No separate Transcript tab

The session view had a Transcript tab next to Workspace and Terminal. Since the Workspace became
the default surface (`docs/agent-workspace.md`), both read the same `…/conversation` page
through the same `loadTranscript`/`refreshTranscript` code and showed it twice, once as cards
and once as a timeline. The tab was removed (#113); nothing the backend serves changed.

What the tab had and where it is now:

- **The conversation, native or scrollback, with markdown and diagrams**: the Workspace timeline,
  which already drew both sources (*Conversation* / *Terminal history*) and mermaid diagrams. It
  now renders the person's turns as markdown too, as the tab did.
- **"[no saved transcript]" for a session with nothing saved**: the timeline used to invite "Give
  your agent a task" whenever it was empty, also on a finished run or a read-only share, where no
  composer exists. It now says *No saved conversation.* unless the person can give this session a
  task.
- **Retry on a failed load and following a running session**: already shared code.
- **Download, a raw/clean switch and search** were not features of the tab. Search exists in the
  Workspace (*Find in conversation*); the cleaned terminal text of an ended session is still
  replayed in the Terminal view (`GET …/transcript`); the raw scrollback stays an internal
  resume input, as above.

Kept, because they do not depend on the tab: a shared link (it renders the same session view,
read-only, and opens on the Workspace by default), the remote API and the MCP tools
(`session_logs`, `session_transcript`), and the chat relays.

Rejected: keeping the tab for read-only viewers only. They get the Workspace without a composer,
which is the same page, and a second rendering of the conversation is a second place for the
two to drift apart, as the markdown styles already had once.

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

For the live read (#111): a pod from an older runtime image answers `/agenthub/transcript` with
404 and is followed through the stored copy, now read freshest first; it keeps its expiring urls
until it is replaced. A hub older than the image answers `POST …/artifact-urls` with 404, and the
agent keeps the urls it has.

Nothing changes for sessions created before this: no `transcript.jsonl` exists,
`GetConversationAsync` returns null, and every surface serves what it served before. A runtime
image older than the hub ignores `AGENTHUB_TRANSCRIPT_PUT_URL`; a hub older than the image answers
the new `PUT …/transcript` with 404, which the agent treats like any other failed best-effort
upload.

## A live read confirms the pod before sending its token (#133)

The status IP is advisory: a deleted pod's address can already belong to another session.
Before **each** transcript HTTP round, the hub checks the cluster for a running, non-deleting
pod named `session-<id>` with the session and agent labels and the same IP. An absent, changed
or unconfirmable identity returns the stored copy without sending the callback token. This uses
the existing live-pod resolver, with no confirmation cache; even the second round and the next
poll must confirm again. The real HTTP regression exercises the actual cluster SDK transport,
a stale IP before the first read, a valid read, and IP reuse after that successful read.

Rejected: trusting the earlier session status or briefly caching a positive confirmation. Both
retain the stale-IP window. A challenge protocol would keep the token off the wire altogether,
but needs a coordinated runtime rollout; a transcript-only credential likewise needs new
issuance and validation on both sides. Fresh cluster confirmation works with existing pods.
There is still a race between the confirmation and the network request; eliminating that race
requires the challenge protocol or authenticated transport to the pod.

Four 8 MiB rounds are sequential, not four simultaneous bodies per viewer. The singleton now
admits one download/parse read at a time across sessions and streams each response into an
enforced 8 MiB cap, with a five-second body deadline. Automatic redirects are disabled: their
destination has not passed the identity check. Cluster confirmation also has a five-second
deadline, and an unavailable cluster sends the caller to the stored copy.

A rootless real-HTTP probe opened 24 distinct sessions, each consuming four exactly 8 MiB
JSONL chunks (96 requests, 768 MiB transferred). The chunks contained an ignored record so the
probe isolated transient parsing costs rather than retained conversation text. With four readers
and an unconstrained heap it reached 1,091,224 KiB peak RSS and allocated 5,541 MiB in total.
With a 384 MiB heap budget (the default 75% of the chart's 512 MiB backend limit), both four and
two admitted readers exhausted the heap. One reader completed all 96 requests with 441,980 KiB
peak RSS and 128 MiB managed memory at the end. The suite also checks that while one session holds the slot, 23 other sessions finish
with the stored-copy fallback before that first read is released; a subsequent read can use
the slot again. Oversized responses fall back without parsing.

Rejected: lowering only the rounds per poll, which slows catch-up but leaves simultaneous viewers
unbounded; two or four active readers, which failed the constrained probe. Serialization trades
catch-up latency for memory headroom. These measurements include the probe host and server,
not the full backend under production traffic. Parsed history remains proportional to followed
session history and is evicted after ten idle minutes; the admission limit is not a total process
memory bound. Production-scale retained-history memory still requires measurement under the
deployment's session and history sizes.

### Multiple viewers and a slow pod

The single slot protects the backend heap across all users; a per-session gate alone would
allow every distinct session to allocate an 8 MiB response and its parser state simultaneously.
Multiple viewers of one session share its existing offset/history rather than separate caches.
Admission to the global slot is non-blocking: when another session already holds it, the reader
returns null immediately and the conversation path serves the stored copy. A slow pod therefore
cannot put unrelated sessions in a global wait queue. Later Workspace polls retry live admission.

Rejected: awaiting the global slot, which makes one stalled pod delay every queued viewer;
per-session admission only, which loses the measured aggregate memory bound; and two concurrent
live readers, which exhausted the constrained heap in the probe above. The tradeoff is freshness:
under sustained contention a session may keep receiving its periodically uploaded transcript.
There is no fairness or one-second live-update guarantee under load; production measurements
may justify smaller chunks and a fair scheduler later. The recorded 24-session memory probe used
queued admission to measure all 96 reads; non-blocking admission keeps the same single-reader
allocation bound while avoiding that queue.

### Clearing a running conversation

The launched process owns the active conversation, rather than the first transcript the
session agent finds. Claude's pinned CLI updates `~/.claude/sessions/<pty-pid>.json` on
`/clear`. Codex's managed `SessionStart` and `UserPromptSubmit` hooks report its rollout
and thread ID; the hook records process ancestry so discovery can match the launched PTY.
Title helpers have no transcript path and are ignored. This rejects newest-file discovery:
a shell-tab CLI or a subagent can write a newer transcript without changing this session.

Discovery runs on live reads and persistence. A switch resets upload deduplication, updates
archived resume metadata, and emits **Conversation cleared**. The native conversation file
stays untouched. Prior conversations and clear boundaries are archived as a Workspace history
prefix; live reads and uploads concatenate that prefix with the active native transcript.
Logical byte offsets include history, so previous turns remain readable above each boundary.
A stable history identity lets later clears append naturally; `X-Transcript-Id` resets a
reader transitioning from a single native file to combined history, even when it grew.
Resume catch-up extracts only the segment after the final boundary: copying the complete
Workspace view into the native transcript would silently undo the context clear. Keeping
history only in backend memory was rejected because pause/resume and an unopened Workspace
must retain it too. Discovery also polls while no viewer is attached.

Claude resumes the recorded session ID; Codex resumes the recorded thread ID. OpenCode's
message plugin records the top-level session and resumes with `--session` after `/new`.
Older archives without a marker retain the provider's existing fallback. OpenCode still
has no native Workspace transcript: its conversation lives in its database and the Workspace
uses terminal scrollback. Exporting that database is a separate change; this fix does not
introduce a second transcript format or couple discovery to its database schema.

Verified on 2026-10-18 with the pinned Claude 2.1.287, Codex 0.160.0 and OpenCode
1.18.35 CLIs against local recording model endpoints. Claude `/clear` and Codex `/new`
were driven through real PTYs inside the common session server, then that server was
restarted: the native transcript excluded the before-clear turn and included both the
after-clear and resumed turns. The uploaded/readable view retained all three above/below
the boundary. OpenCode's recorded new session matched its real database and accepted a
new turn when launched with `--session`. Chromium drove the real Workspace composer
through the real backend live reader and verified owner history above the boundary and
new turns below it. These are local runtime proofs, not a rebuilt image or cluster rollout.
