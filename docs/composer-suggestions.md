# Native composer suggestions

The Workspace displays the running CLI's prediction and accepts it into the person's draft
with Right Arrow or Tab. Acceptance writes no terminal input. Sending still requires Enter or
Send, and an untouched prediction is not content that can be sent or queued. Typing dismisses
it; it cannot overwrite an existing draft. Disconnect, menus and a new working turn hide it.

The provider reader is deliberately small (`frontend/src/lib/prompt-suggestion.js`). Only
Claude has a confirmed source in the current runtime pins. Its interactive driver enables the
native generator unless the operator explicitly configured that switch already. This retains
the CLI's eligibility checks, including focus and conversation history.

## Why read the live screen

Pinned Claude 2.1.287 exposes `prompt_suggestion` in print/SDK mode. The Workspace runs its TUI,
which does not emit that event into the terminal or ordinary transcript (the generation uses
`skipTranscript`). Switching transport solely for predictions would replace the person's live
terminal session. Instead the reader checks the active xterm buffer, cursor column 2, the
Claude prompt between its horizontal rules, and dim attributes on every text cell. Native
Right Arrow removes dim styling and moves the cursor to the end. Ordinary text at Home is
not dim. Welcome examples beginning with `Try "` are excluded. Narrow terminals truncate longer
predictions with an ellipsis; those are omitted because the screen no longer holds the exact
text. Widening the terminal makes the full native prediction eligible again. If those invariants change, the
reader offers nothing rather than interpreting earlier assistant prose as a prediction.

We rejected generating a second Workspace prediction: it would need another model request,
would lack the CLI's exact conversation and could disagree with the terminal. We also rejected
copying any grey placeholder: the pinned Codex placeholder is not an actionable suggestion.

## Provider findings for issue #257

Real Codex 0.160.0 in a PTY, after one and two completed recording-model turns, returns to
`Ask Codex to do anything`. Right Arrow and Tab leave that placeholder unchanged. In its exact
`rust-v0.160.0` source, `chatwidget.rs` defines the static placeholder; the composer renders it
dim when empty. There is no next-prompt generator or protocol event in the source. References
to `next_prompt_suggestion.rs` survive only as text in markdown rendering fixtures. No Codex
reader is enabled pending a real reproducer for the owner's reported behavior.

Real OpenCode 1.18.35 returns to an empty composer after a completed recording-model turn. Its
initial `Ask anything…` prompt is a rotating example. No next-message prediction was observed;
this does not prove the absence of a separately gated feature. No reader is enabled.

Real Claude 2.1.287 generates a dim `run the tests` after two completed turns against the
recording model with its supported generator switch enabled. The production reader extracts
that exact text and returns empty after native Right Arrow accepts it.

## Reproduction

`proofs/257/pty-rig.mjs` uses the installed pinned Codex, the pinned Claude/OpenCode executables
under `PROOF_CLIS` (default `/tmp/proof257-clis/node_modules/.bin`), real node-pty and headless
xterm from `/opt/session-agent/node_modules`. It accepts a JSON array of steps and records
screens, cursor/cell attributes and local model request classifications. Generated homes and results are
ignored. The `assertSuggestion` step runs the production reader against the real terminal.

`proofs/257/browser.mjs` copies this branch's runtime into an isolated temporary directory and
runs the real common server and Claude driver, a recording model, a stand-in hub and the
branch's Vite frontend. `PROOF_CDP` identifies the session's real Chromium endpoint;
`PROOF_BROWSER_URL` is the Vite URL reachable from that browser. It checks the native text in
an empty Workspace composer, both acceptance keys with zero PTY input, Enter on untouched
prediction with zero PTY input, explicit edited sending reaching the model, and dismissal on
typing. The hub only supplies session metadata and an empty conversation; this is not a backend
controller, cluster, deployed account or transcript-parser proof.

Validation on this branch: 860 frontend tests and 750 session-agent tests pass; the production
frontend builds. The real PTY width proof rejects a truncated 45-column prediction, extracts
its complete text at 120 columns, then returns empty after native acceptance. The Chromium
proof passes both acceptance keys with zero PTY input and confirms the edited send at the
recording model. The initial local full-runtime run needed physical dependency links at both
runtime levels for ESM imports; `NODE_PATH` alone does not reproduce the runtime image layout.
