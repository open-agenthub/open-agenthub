# Conversation Transcript Design

## Problem

Terminal-session transcripts are readable after control-sequence sanitization, but
fullscreen provider TUIs persist large amounts of real newline whitespace. The current
single `<pre>` renders that whitespace literally, producing long empty areas.

Unlike chat sessions, terminal scrollback does not reliably identify user, assistant,
thinking, or tool events. The UI must not invent conversation roles from provider-specific
prompt characters.

## Approved Visual Direction

Render terminal transcripts as a chronological vertical stream inspired by the approved
"wide agent bubbles" mockup:

- Every transcript block is a wide, left-aligned neutral `Terminal` bubble.
- Bubbles appear strictly in source order with compact vertical spacing.
- The existing warm dark product palette, monospace transcript type, and responsive
  760-pixel reading column remain in use.
- No user/assistant labels, timestamps, tool states, or Markdown semantics are inferred.
- Chat-mode sessions keep their existing structured `ChatPane` rendering unchanged.

## Transcript Block Formation

Add a framework-free frontend helper that accepts the backend's sanitized plain text and
returns an array of display blocks:

1. Normalize CRLF and carriage returns to line feeds.
2. Treat two or more consecutive whitespace-only lines as a block boundary.
3. Remove remaining whitespace-only lines inside each block so terminal layout gaps do
   not reappear inside a bubble.
4. Trim each block and discard empty blocks.
5. Preserve every non-empty line and its characters verbatim.

An empty or whitespace-only transcript returns no blocks and continues to display the
existing `[no saved transcript]` empty state.

This is presentation-only processing. Raw S3/Postgres scrollback and the backend
plain-text transcript contract remain unchanged.

## Components and Data Flow

- `frontend/src/lib/transcript.js` owns deterministic plain-text-to-block conversion.
- `TerminalView.vue` computes blocks from `transcriptText` and renders them as neutral
  bubbles in the existing Transcript tab.
- Owner and shared-session transcript fetch paths continue to use their existing API
  functions and receive identical rendering.

## Accessibility and Responsive Behavior

The transcript remains selectable plain text. Each block uses semantic text markup inside
a labeled transcript region. Bubbles use existing theme variables, maintain readable
contrast, wrap long text, and expand to the available width on narrow screens.

## Testing

Use test-driven development for:

- Large blank runs becoming separate ordered blocks with no rendered blank lines.
- CRLF/carriage-return normalization.
- Preservation of non-empty terminal lines and whitespace inside those lines.
- Empty input returning no blocks.
- `TerminalView` rendering neutral bubbles for normal and shared transcript paths while
  retaining the existing empty state.
- Existing chat-mode behavior and the complete frontend suite remaining green.
