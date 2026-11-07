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

The external coordinator queues for the production lifecycle advisory key before killing a
backend. It briefly parks competing workers on that advisory lease while the replacement
initializes, then releases it; it holds no table or row lock during startup. A held journal
write plus queued schema DDL can otherwise block the proof's own observer. The real-PostgreSQL
self-test reproduces that convoy and verifies handoff, schema completion and worker yield.
Observer commands have statement/lock/process deadlines, and cleanup commits gate release
before dropping the trigger. The application exposes no fault-control endpoint.

## Explicit context clear before native recovery

```sh
PROOF_AGENTS=codex PROOF_CONTEXT_CLEAR_BEFORE_RESUME=1 \
PROOF_SOURCE_PREPARE=1 PROOF_RESUME_ARCHIVE=1 PROOF_NATIVE_DEFAULT=1 \
PROOF_PROOT=/absolute/proot PROOF_OUT=/absolute/scratch/path \
PROOF_CLI_BIN=/absolute/pinned/cli/bin node tests/ProviderRuntimeProof/capture.mjs
```

The managed real CLI clears its context, creates a new native ID, checkpoints and exits.
The archive is restored into fresh home contents under the same path and managed runtime.
Resume must select the checkpoint's active ID, not the original registry ID. The new owner
request must include the post-clear task and omit the earlier cleared task. The local hub
policy/model callbacks are fixtures; this does not establish backend replica recovery.

## Ordinary session new-thread compatibility

Use the managed Codex archive command above with
`PROOF_ORDINARY_NEW_RESUME=1` instead of `PROOF_CONTEXT_CLEAR_BEFORE_RESUME=1`.
The real terminal starts without any provider-switch request, runs an initial turn, sends
`/new` and a second turn, then stops and restores its actual archive. Resume has no strict
replacement flag. Its new owner turn must use the second native ID and context, while the
production transcript retains both visible turns. This compatibility control passed locally;
it is not a backend generation-switch or whole-browser acceptance result.

## Native target process death

Run the local archive capture command with `PROOF_CRASH_NATIVE_TARGET=1` instead of
`PROOF_RESUME_ARCHIVE=1`. After the real native handover answer, the rig kills the isolated
server's single actual PTY child. The runtime must exit unsuccessfully without starting a
fresh CLI or making another handover request. The signal fix is isolated in #537; this control requires that patch. Both pinned CLIs passed after signal-aware
exit classification; before the fix, the real PTY supplied code zero plus signal 9 and the
runtime incorrectly reported success. This proves native-process handling, not kubelet
restart policy, pod-loss reconciliation or backend crash recovery.
