# Transcript Noise Compaction Design

## Problem

The initial conversation-style transcript view exposes a second property of fullscreen
terminal UIs: animation frames and incremental screen redraws are persisted as ordinary
text after ANSI control sequences are removed. A representative local transcript contains
7,058 display blocks, of which 6,980 contain at most twelve visible characters. Examples
include spinner glyphs, isolated digits, timer/token counters, and character-by-character
redraw fragments.

Rendering each fragment as its own bubble makes the transcript longer and less readable
than the original raw `<pre>`. The display layer needs balanced noise compaction without
discarding isolated legitimate short commands or outputs.

## Selected Approach

Extend the existing framework-free frontend transcript helper. Raw S3/Postgres transcript
data, the backend plain-text contract, and chat-mode rendering remain unchanged.

The alternative of replaying raw ANSI through a terminal emulator would primarily recover
one final screen, not a useful chronological conversation. Semantic runtime events would be
a better long-term source but would not improve existing transcripts. Frontend compaction
therefore provides the useful behavior now while remaining reversible and testable.

## Compaction Pipeline

`toTranscriptBlocks(text)` continues to normalize and split the sanitized plain text, then
applies the following ordered stages:

1. **Base block formation** keeps the approved behavior: normalize CRLF and carriage
   returns, use two or more whitespace-only lines as a boundary, remove remaining
   whitespace-only lines, and preserve every character on non-empty lines.
2. **Known transient removal** discards single-line blocks made only from the known Claude
   spinner glyphs (`*`, `·`, `…`, `✢`, `✣`, `✳`, `✶`, `✻`, `✽`, and their combinations) and
   single-line elapsed-time/token counter fragments such as `10s · ↓ 1.0k tokens)` with an
   optional leading spinner glyph. Digits alone are not unconditionally transient.
3. **Short-burst suppression** classifies a block as short when its trimmed content has at
   most twelve Unicode code points and at most two non-empty lines. A run of four or more
   consecutive short blocks is treated as one incremental redraw/animation burst and
   removed. Runs of one to three short blocks remain, preserving isolated commands and
   small outputs such as `ls`, `42`, and `OK`.
4. **Adjacent redraw deduplication** collapses immediately adjacent blocks whose content is
   identical after trimming and collapsing whitespace. The later source block is retained,
   so display order and the most recent redraw representation win.
5. **Bubble packing** joins adjacent surviving source blocks with two line feeds until the
   next addition would exceed 1,200 Unicode code points or twenty source blocks. A single
   source block larger than 1,200 code points remains intact in its own bubble.

The stages operate in source order and never reorder retained content. If compaction removes
everything, the existing `[no saved transcript]` state is displayed.

## Components and Data Flow

- `frontend/src/lib/transcript.js` owns base block formation, transient classification,
  burst suppression, deduplication, and packing.
- `TerminalView.vue` continues to consume only `toTranscriptBlocks(transcriptText)` and
  requires no new rendering branches.
- Owner and shared-session transcript paths receive identical compaction.
- Structured chat sessions continue to use `ChatPane` and bypass this terminal transcript
  logic.

## Safety and Failure Behavior

The compaction is presentation-only: a false positive cannot mutate stored history, and a
future heuristic can re-render the same raw transcript differently. Rules deliberately use
burst context rather than deleting every short block. Transcript content remains rendered
through Vue text interpolation, so compaction introduces no HTML interpretation.

## Testing and Acceptance

Use test-driven development with literal fixtures that demonstrate:

- Known spinner-only and timer/token blocks are removed.
- Four or more consecutive short redraw fragments are removed.
- One to three short blocks, including `ls`, `42`, and `OK`, remain.
- An isolated digit remains outside an animation burst.
- Whitespace-equivalent adjacent redraws collapse to the later block.
- Retained blocks pack chronologically without exceeding twenty source blocks or the
  1,200-code-point target, while oversized blocks remain intact.
- Empty/noise-only input produces no blocks and keeps the existing empty UI state.
- Owner, shared, loading, empty, and chat-mode component behavior remains green.

Acceptance against the local Docker Desktop sample requires a large reduction from the
current thousands of tiny bubbles, no standalone spinner/timer bubbles in the inspected
region, and preservation of isolated short legitimate content in automated fixtures.
