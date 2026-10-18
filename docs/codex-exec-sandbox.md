# Codex exec uses the session pod as its isolation boundary

Autonomous and scheduled sessions use `codex exec --sandbox danger-full-access`.
The session pod provides isolation: the process remains non-root, without added
capabilities, under the pod's filesystem and network restrictions. This setting
removes only the CLI's inner shell sandbox; it does not grant pod privileges.

The previous `workspace-write` setting invokes bubblewrap, which needs user
namespaces the session pod deliberately cannot create. With exec's effective
`approval_policy=never`, every shell command failed before execution and there
was no interactive escalation path. Changing the approval policy alone would
leave unattended work waiting for a person.

Managed `PreToolUse` hooks still check the session policy before execution. A
hard deny blocks the tool. With full access the CLI no longer raises
`PermissionRequest` automatically for a shell command, so an unattended policy
`ask` is resolved in `PreToolUse` through the same backend permission endpoint,
including polling, redacted approval descriptions, and the final policy
recheck. Missing or invalid answers fail closed. The managed pre-tool timeout
matches the permission hook's approval budget. Interactive permission handling
continues through `PermissionRequest`.

We rejected enabling user namespaces, adding privileges/capabilities, or
loosening the pod's seccomp profile to accommodate an inner sandbox. Those
choices weaken the pod boundary and tie ordinary agent execution to cluster
security exceptions. `read-only` still needs the unavailable namespace setup.
We also rejected the combined approvals-and-sandbox bypass flag: selecting only
the sandbox mode keeps approval behavior explicit and leaves managed hooks in
place.

## Real CLI proof

`agent-runtime/session-agent/fixtures/codex-sandbox-smoke.js` runs the version
pinned by the runtime Dockerfile against a local mock Responses endpoint and a
recording policy/permission endpoint. A process-local requirements binding
loads the branch's actual managed hooks without changing the live session's
policy. Run in a Linux session pod without user namespaces:

```bash
CODEX_BIN=/absolute/path/to/pinned/codex \
PROOT_BIN=/absolute/path/to/proot \
NODE_PTY_MODULE=/absolute/path/to/node-pty \
node agent-runtime/session-agent/fixtures/codex-sandbox-smoke.js
```

The pinned 0.160.0 CLI reproduced the original bubblewrap namespace failure.
The new driver command wrote the expected marker; a hard policy deny left it
absent and returned a pre-tool denial to the model. An uncovered command reached
`/permission`: approval allowed execution only after another policy check, and
denial prevented execution. A project config attempting to disable hooks did
not disable the managed gate.

The interactive driver does not specify a sandbox mode. With the entrypoint's
trusted-project configuration, the pinned TUI defaults to `workspace-write`
and permits approval requests. A normal shell call still hits the same
namespace error. Interactive sessions work by approving an escalated command,
which runs outside the inner sandbox; they do not demonstrate that bubblewrap
works in the pod. The exec setting chooses that effective execution boundary
up front for unattended work.

This proof uses the real CLI, hooks, shell, and PTY, with synthetic model and
backend responses. It does not build the runtime image or exercise a deployed
backend/cluster; image smoke tests and CI cover the packaged runtime separately.
