# Clean Transcript Design

## Problem

Session scrollback contains raw terminal control sequences. The transcript API returns
that scrollback unchanged, so the web transcript displays escape codes such as cursor
save/restore, screen-mode, cursor-positioning, and color commands as visible text.

## Design

Keep the stored scrollback unchanged. Raw terminal data remains available for terminal
replay and future rendering improvements.

Sanitize the text at the backend transcript boundary:

1. Load the preferred S3 scrollback or the Postgres fallback as today.
2. Pass the selected text through the shared terminal-control sanitizer.
3. Return the sanitized plain text to every transcript consumer.

This location covers owner and shared-session transcript endpoints without duplicating
logic in the Vue frontend. It also avoids permanently discarding information when the
runtime stores scrollback.

## Sanitizer Behavior

Extend the existing `AgentTerminal.StripAnsi` implementation to remove the terminal
control sequences present in provider TUIs, including:

- CSI sequences for colors, cursor movement, mode changes, and screen operations.
- OSC sequences terminated by BEL or ST.
- Short ESC sequences such as save and restore cursor.
- C1 control characters that can otherwise remain visible.

Normalize carriage-return line endings to line feeds as before. Preserve ordinary text,
spacing, and line feeds.

The sanitizer produces readable plain text; it does not emulate a terminal screen or
reconstruct overwritten rows.

## Error Handling and Compatibility

Missing sessions and missing transcripts retain their current behavior. Sanitization is
deterministic and local, with no new dependencies or network calls. Existing plain-text
transcripts remain unchanged.

## Testing

Add regression tests before implementation:

- The reported Claude trust-screen sequence is returned without escape/control bytes.
- Existing CSI color removal and newline normalization remain correct.
- Plain text remains unchanged.
- Transcript retrieval sanitizes both the S3 result and the Postgres fallback.

Run the focused backend test suite, followed by the complete backend test suite.
