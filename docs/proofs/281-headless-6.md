# Headless terminal 6 migration

The session transport uses the public CommonJS `Terminal` export, buffer lines and write
callbacks. Version 6 keeps those APIs. Its new package exports map rules out importing internal
files; the runtime already imports the package root. Removed `windowsMode` and
`fastScrollModifier`, browser overview-ruler/scrollbar changes and keyboard mapping changes do
not apply to these headless readers. Adding browser keybinding compatibility shims was rejected
because they would change CLI input without addressing a headless API change.

Version 6 also parses synchronized-output mode. The proof compares visible cells after real
PTY output settles, rather than demanding identical intermediate redraw events.

Upstream changes: https://github.com/xtermjs/xterm.js/releases/tag/6.0.0

## Evidence so far

- Complete session-agent suite: 796 passed, zero failed/skipped, under Node 26.12.1.
- Real pinned Codex 0.160.0 device login: the same PTY frames fed to 5.5 and 6 produced identical
  visible cells, sign-in classification, menu classification and private export buffer.
- Reproducer: `scripts/proofs/281/compare.cjs`. Set `PROOF_ARGS` to
  `["login","--device-auth"]` and `PROOF_EXPECT=signin`; it defaults to the installed CLI and
  baseline image modules. Candidate modules come from this worktree. It uses a fresh home,
  keeps authorization data in memory and removes that home afterwards.

Real pinned OpenCode 1.18.35 provider selection through credential entry also produced identical
visible cells, sign-in/menu classifications and private export buffers (7 PTY frames).
Question, remaining CLI and browser proofs are still pending. This evidence does not establish
real-account authorization or deployed-cluster behavior. The local suite used the image's
compiled node-pty; rebuilding it here requires build tools absent from this session.
