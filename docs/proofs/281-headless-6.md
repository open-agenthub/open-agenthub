# Headless terminal 6 migration

The session transport uses the public CommonJS `Terminal` export, buffer lines and write
callbacks. Version 6 keeps those APIs. Its new package exports map rules out importing internal
files; the runtime already imports the package root. Removed `windowsMode` and
`fastScrollModifier`, browser overview-ruler/scrollbar changes and keyboard mapping changes do
not apply to these headless readers. Adding browser keybinding compatibility shims was rejected
because they would change CLI input without addressing a headless API change.

Version 6 also parses synchronized-output mode. Compare visible cells after real PTY output
settles, rather than demanding identical intermediate redraw events.

Upstream changes: https://github.com/xtermjs/xterm.js/releases/tag/6.0.0

## Measured real CLI screens

`scripts/proofs/281/compare.cjs` feeds the same actual node-pty frames to both releases. It
asserts identical visible cells, sign-in detection, Workspace menu detection and private export
buffers. This avoids comparing independently generated authorization codes or redraw timings.
The baseline was the image's installed 5.5.0, the candidate this branch's 6.0.0. Node was
26.12.1; native node-pty was 1.1.0. Each CLI used a new isolated home, removed after the run.
Only counts and classifications were logged; authorization data stayed in memory.

| Actual CLI | Screen | Result |
|---|---|---|
| Claude Code 2.1.287 | Login code, model selection, native AskUserQuestion | Identical |
| Codex 0.160.0 | Device login | Identical |
| OpenCode 1.18.35 | Provider selection through API-key entry | Identical |
| OpenClaw 2026.7.1-2 | Provider/method selection through token entry | Identical |
| Cursor 2026.10.22-27dcf5f | Browser login | Identical |

Cursor is deliberately unpinned; the table records the installer build observed during this
proof. The other versions match their runtime Dockerfiles.

The full session-agent suite passed: 796 tests, zero failures or skips. The complete frontend
suite also passed: 904 tests across 101 files.

## Real browser and transport

The existing `scripts/proofs/242/browser-hub.cjs` copies the actual common server and drivers;
`SIGNIN_NODE_MODULES` selects the baseline or candidate installed dependency tree. Its default
remains the image's modules. The existing `browser-check.cjs`, with `SIGNIN_CREDENTIAL_ONLY=1`,
passed provider selection through real credential-card display in Chromium with both trees.

`question-model.cjs` returns a native AskUserQuestion tool call. `question-browser.cjs` uses the
actual Workspace, real common server and pinned CLI to assert the entire wrapped question,
free-text input and a newline-preserving answer returned by the CLI as a tool result. Resetting
the local model's result before each run prevents a previous run from satisfying the answer
assertion. The same check passed with baseline and candidate dependency trees.

To repeat the question proof, start the local model, start the existing hub with
`SIGNIN_QUESTION_MODEL` pointing to it and `SIGNIN_CLI_BIN` pointing to installed exact pins,
serve this branch's frontend, and run `question-browser.cjs` with this session's browser CDP
and UI addresses supplied through `SIGNIN_CDP` and `SIGNIN_UI_HOST`. Repeat the hub with and
without `SIGNIN_NODE_MODULES` selecting this branch's session-agent node_modules.

For PTY comparisons set `PROOF_CLI`, `PROOF_ARGS` and `PROOF_STEPS` (JSON). Steps wait for
literal native text with `ready`, send exact bytes with `keys`, or wait a bounded number of
milliseconds. Set `PROOF_EXPECT=signin` or `choice` to prevent an empty screen from passing.
`PROOF_QUESTION=1` supplies the isolated fake model and fake API key for native question/menu
runs. The observed ready text is `auto mode on`; the question footer contains `to navigate`.

No real-account login success, external model call or deployed-cluster behavior is claimed.
Local native rebuilding was unavailable because the session lacks build tools; PR image-build
CI provides clean builder verification on both architectures.
