# Pending target runtime proof

Run the pinned Claude and Codex binaries through the production session server, with
isolated homes, synthetic credentials and recording local model/archive endpoints:

```sh
PROOF_OUT=/tmp/provider-target-proof \
PROOF_CLI_BIN=/absolute/path/to/pinned/node_modules/.bin \
node tests/ProviderRuntimeProof/capture.mjs
```

The script requires the image's `ws`, `node-pty` and `@xterm/headless` modules under
`/opt/session-agent/node_modules`. It proves each actual CLI records its initial prompt,
reports native identity through the production pending-target route, blocks ordinary
terminal input before release, uploads a nonempty native archive before release, then
permits release and terminal reconnect without replacing its native history. The terminal
emulator answers startup negotiation while the target is reserved. Output remains in the
specified scratch directory; never commit native transcripts, archives or logs.

This is a target-side protocol proof, not an end-to-end provider switch. Source safe-point
hooks, native account discovery/capacity admission, Kubernetes replacement, persistent volume
mounts, real provider authentication and backend recovery require separate proofs. By default Codex uses
a local custom model provider to make this transport test deterministic. Set
`PROOF_NATIVE_DEFAULT=1` to retain its built-in provider/default model and redirect only
its native API base using the user-level `openai_base_url` setting. The pinned CLI ignores
`OPENAI_BASE_URL`; verify the recorded destination rather than assuming that environment
variable works. A hardcoded retired model opens a migration menu before any request, so
the native-default scenario deliberately leaves the model unset. Neither recording model
response supplies a context-window capability. Model replies and
archive storage are stand-ins; both CLIs and the session transport are real.

Run the runtime suite with `TMPDIR=/tmp` and `umask 022`: existing fixtures assert paths under
`/tmp` and a chmod from a normally created file to mode 0600. A session-wide restrictive 0077
mask makes that chmod a no-op and causes an unrelated archive-signature assertion to fail.

For a positive source safe-point proof, also set `PROOF_SOURCE_PREPARE=1` and point
`PROOF_PROOT` at the scratch static binary used by the fleet-delivery real-CLI rig. The
script installs the actual Claude settings hooks. For Codex it copies this branch's runtime
and requirements into scratch space and runs the whole session server inside proot, with
private `/tmp` and image-owned managed hooks. It binds the isolated proof home/workspace and
pinned CLI installation separately so the private `/tmp` cannot hide them. No native turn
events are injected: real UserPromptSubmit/Stop hooks establish the completed safe point.
After target release, each CLI reserves a subsequent source request, checkpoints its archive,
and cancels that source reservation. It then reserves again, submits the native `/exit` command
with text and Enter separated to avoid paste detection, waits for actual PTY exit and
acknowledged final persistence, and checks cancellation is refused after shutdown starts. Shell/background and concurrent guards have separate
runtime regressions; no real provider account is used.

`node tests/ProviderRuntimeProof/signature-mode.mjs` exercises actual filesystem metadata
through the production traversal before its final digest. It requires a real coarse-ctime
collision, compares the same metadata snapshots with/without mode, and asserts production
signatures distinguish different permission modes. The script refuses to claim reproduction
when the filesystem does not exhibit a collision within its bounded attempts.

With `PROOF_SOURCE_PREPARE=1`, cancellation also verifies a queued message becomes native
Codex input without a subsequent human turn. Add `PROOF_CLAUDE_MOD=1` to load a private copy
of the shipped Claude mod against the local transport and verify acknowledged mod handback.
The copy redirects only loopback ports. Nonessential-traffic suppression must be unset for
that native mod, whose loopback HTTP uses the CLI's networking API.
