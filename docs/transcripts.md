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
