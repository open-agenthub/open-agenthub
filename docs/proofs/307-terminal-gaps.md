# Terminal transcript spacing

The transcript reader deletes terminal controls from stored PTY bytes. Claude
Code can position text using cursor-forward and absolute-column controls instead
of spaces; deleting those controls joins otherwise separate words.

Preserve those horizontal gaps before removing the remaining controls. Bound
expansion to 4096 cells per move so untrusted terminal output cannot request an
unbounded allocation. Colour controls continue to have no text width.

Adding spaces between arbitrary words was rejected because it would corrupt
filenames and code. Changing frontend typography was rejected because the spaces
are already absent from the backend response. This retains the existing terminal
history model rather than replacing history with a single final screen.

scripts/proofs/307/capture.cjs launches the exact 2.1.287 native CLI in a real PTY,
answers terminal queries through the real headless renderer and captures a
synthetic, isolated startup. The regression checks real absolute-column and
cursor-forward gaps alongside targeted word-spacing cases.

Validation: replacing the converter with origin/main makes 6 of the 14 spacing
and control tests fail, including the real startup fixture. With the fix, the
full backend suite passes: 2302 passed, 159 database-dependent tests skipped.
A separate expansion-bound test covers oversized and overflowing cursor counts.
Total additional whitespace is bounded as well as each individual move.

The final suite also ran against a real rootless Postgres 15 instance using
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING: 2461 passed, 0 failed, 0 skipped.
