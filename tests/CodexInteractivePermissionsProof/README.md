# Codex interactive permissions proof (#451)

Runs the Dockerfile-pinned real CLI, its managed hooks and a real shell in a PTY.
The model and policy/permission endpoints are synthetic loopback services. A fresh
trusted Git workspace and explicit synthetic credentials isolate each case.
No Docker, cluster access or real model credentials are needed.

```sh
CODEX_BIN=/absolute/path/to/pinned/codex \
PROOT_BIN=/absolute/path/to/proot-launcher \
NODE_PTY_MODULE=/opt/session-agent/node_modules/node-pty \
node tests/CodexInteractivePermissionsProof/proof.cjs
```

Use proot 5.4 or later. The script disables its seccomp acceleration: on the proof
host the accelerated path silently ignored file bindings, while 5.1 crashed TUI
hook subprocesses. Requirements and the managed runtime are bound for this child
only; the running session's policy is never changed. If the tracer needs a newer
C library, launch it with that library's loader and `--library-path`, rather than
changing the global library path for the CLI and hook children.

The twelve cases assert marker existence and the tool result sent back to the model:
exec allow/deny/approval allow/approval deny; TUI normal command/native escalation
rejection/delayed approval/approval allow/approval deny, plus expired approval,
backend restart recovery and unavailable approval with an actionable denial reason. Branch cases also reject
hook failures and any native approval menu. A checkout attempting to disable hooks
does not disable the managed gate. Each process group has a bounded timeout.

For the real Workspace approval button, serve this branch's frontend with Vite,
start the integrated browser, and add:

```sh
PLAYWRIGHT_MODULE=/opt/session-agent/node_modules/playwright-core \
PROOF_CDP=http://browser.example.com:9222 \
PROOF_UI=http://session.example.com:5173 \
PROOF_ONLY=interactive-overlap \
CODEX_BIN=/absolute/path/to/pinned/codex \
PROOT_BIN=/absolute/path/to/proot-launcher \
NODE_PTY_MODULE=/opt/session-agent/node_modules/node-pty \
node tests/CodexInteractivePermissionsProof/proof.cjs
```

The browser loads the production Workspace against synthetic API responses. Its
Allow POST changes the same approval state polled by the real CLI's policy hook.
The marker must be absent before that click and present after it. This proves the
UI-to-hook decision path, not deployed backend persistence or authorization.

To compare main, extract its `agent-runtime/` into a scratch directory with
`git archive`, set `PROOF_RUNTIME` to that copy's `agent-runtime/codex`, and select
one case with `PROOF_ONLY`. `interactive-overlap` uses a native escalation on main;
`interactive-native-fallback` checks the native-menu fallback after a backend ask
response. The expired-card case also tests `PROOF_ORDER=hub-first|native-first|escape-between`.
Both answer orders complete on 0.166.0; Esc interrupts the turn: the owner's exact both-answered hang was not
reproduced. Do not describe that sequence as a passing before/after reproduction.

For the namespace error, run `PROOF_DIRECT=1 PROOF_ONLY=legacy-workspace-write`
with `PROOF_RUNTIME` set to main. Direct mode uses the image-owned requirements,
so first verify that the image's hook and main's hook have identical SHA-256 hashes.
It avoids tracing the sandbox helper itself. The marker stays absent and the tool
result contains the bubblewrap namespace error.

Evidence is written to a fresh temporary directory printed at startup. Keep all
JSON, terminal captures, screenshots and core dumps out of Git; upload screenshots
to Files and record results in the PR. Only scripts and this README are committed.
