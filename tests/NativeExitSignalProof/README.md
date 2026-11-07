# Native signal-exit proof

Run against the pinned real CLI binaries installed in an isolated scratch prefix:

```sh
PROOF_OUT=/absolute/scratch/path PROOF_CLI_BIN=/absolute/pinned/cli/bin \
node tests/NativeExitSignalProof/prove.mjs
```

The production common server runs with real node-pty and a terminal renderer. Only the
model and hub callback endpoints are local recording fixtures, with synthetic credentials.
For Claude and Codex, answer an initial real turn, then either kill the actual PTY child
with SIGKILL, use the native `/exit` command, or send SIGTERM to the runtime itself. Assert the real status callback is Failed
for signal termination and Succeeded for normal exit, the process status agrees, and no
second initial task reaches the model. Intentional runtime shutdown exits zero without a
terminal Failed/Succeeded callback, leaving pause/update status to the backend. Generated logs/homes stay outside Git.

`PROOF_REPO` can point at a separately extracted released source baseline. Before the fix,
the SIGKILL control reports Succeeded with process exit zero. It passes after the fix.
This is a runtime/process proof; it does not simulate kernel memory pressure, a container
OOM event, Kubernetes pod deletion or provider-switch recovery.
