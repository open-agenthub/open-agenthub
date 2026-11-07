# Provider recovery proofs

Native resume decisions are in `docs/design/provider-switching-native-resume.md`.
The broader recovery acceptance matrix remains separate in #529.
No result is implied by the presence of these scripts.

The first negative control uses the production session server and real pinned native CLIs:

```sh
PROOF_MISSING_NATIVE_STATE=1 PROOF_NATIVE_DEFAULT=1 \
PROOF_OUT=/absolute/scratch/path PROOF_CLI_BIN=/absolute/pinned/cli/bin \
node tests/ProviderRuntimeProof/capture.mjs
```

It simulates a completed-generation resume whose entrypoint failed to restore its archive.
The original session prompt is still present. The runtime must fail before a fresh CLI can
send that stale prompt to the recording model. A real terminal renderer answers native PTY
queries; no native transcript rows are fabricated. Model replies and API keys are synthetic.
Generated output belongs outside Git. This local check does not prove Kubernetes restart or
backend recovery; the combined actual-worker fault matrix remains a separate gate.

Positive archive control:

```sh
PROOF_RESUME_ARCHIVE=1 PROOF_NATIVE_DEFAULT=1 \
PROOF_OUT=/absolute/scratch/path PROOF_CLI_BIN=/absolute/pinned/cli/bin \
node tests/ProviderRuntimeProof/capture.mjs
```

This restores each actual checkpoint archive into a fresh home, reinstalls only synthetic
selected-provider credentials/config, resumes the same native identity and sends a new owner
turn. The recording request must contain the old conversation and the new turn, without the
stale original-source prompt. Wait for native file persistence separately from model receipt:
Codex can send its request before flushing the corresponding rollout record to disk.

Keep the absolute HOME path the same while replacing its contents for the positive control,
as real replacement pods do. Codex's native SQLite index retains absolute rollout paths.
Restoring into a different pathname while leaving the old home accessible can make the CLI
read/write the old file while the transcript route observes the restored copy. Such a run
does not prove archive recovery. Retire the old directory, create an empty home at its old
pathname, and restore only the actual archive plus selected synthetic credentials/config.
