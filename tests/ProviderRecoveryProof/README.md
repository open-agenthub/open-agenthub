# Provider recovery proofs

Native resume decisions are in `docs/design/provider-switching-native-resume.md`.
Track the wider acceptance matrix in `docs/design/provider-switching-recovery.md` and #529.
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

## External persisted-phase crash barriers

`gates.sql` installs a trigger only in the disposable proof database. Before a selected
journal update commits, it holds an observable advisory lock while the previous phase stays
visible. `barriers.mjs` checks the barrier against an isolated real PostgreSQL database: kill
the blocked backend connection, verify rollback, then release and continue. This self-test
proves the fault mechanism, not application recovery.

Dispatch `install-smoke` with `prove_provider_recovery=true` for the actual-worker matrix.
It enables the same real native replacement rig and kills a real backend pod at ten write
boundaries per replacement, including both sides of source-stop acknowledgement, and two
cancellation boundaries. Two backend replicas must recover from the saved phase without
changing authority twice. The stable backend Service address survives pod death; a port
forward pins one backend and cannot serve as the recovery transport. Owner start/retry/cancel
requests retry only their same idempotency key after a lost reply; message sends are not
blindly retried. After each completed replacement, pause/resume the actual native generation
and send a new owner turn, retaining one handover and the same native identity/PVC/history.

The fault trigger is never installed by the application or exposed as a public control. It
is released/dropped during proof cleanup. Record real dispatch outcomes before claiming the
phase matrix passed. Additional runtime/pod/storage/auth faults remain separate acceptance
rows; backend phase crashes do not establish them.

## Native identity inside a multi-conversation archive

Add `PROOF_AGENTS=codex PROOF_POISON_NATIVE_POINTER=1 PROOF_RESUME_ARCHIVE=1` to the
local native archive control. It creates a second conversation with the real CLI, records
its actual native ID, points the restored native metadata at it, then resumes using the
committed identity. Both histories come from actual CLI execution. The resumed model must
receive the original conversation and new owner turn without the other conversation's text.
This checks native identity selection, not a crash/replica result.
