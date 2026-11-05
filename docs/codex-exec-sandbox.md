# Codex uses the session pod as its isolation boundary

Interactive, autonomous and scheduled sessions use `--sandbox danger-full-access`.
The interactive driver also sets `--ask-for-approval never`: AgentHub owns
tool approval. Exec already defaults to `never`. Fresh sessions, resumes and
device-login handoffs share these settings.
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
`PermissionRequest` automatically for a shell command, so a policy
`ask` in every session mode is resolved in `PreToolUse` through the same backend permission endpoint,
including polling, redacted approval descriptions, and the final policy
recheck. Missing or invalid answers fail closed. The managed pre-tool timeout
matches the permission hook's approval budget. Interactive callback errors and expired approvals also deny the tool. Keeping
a native-menu fallback would introduce a second decision path. The managed
`PermissionRequest` hook remains as a defensive gate for CLI requests that still
raise that event; ordinary full-access calls are decided in `PreToolUse`.

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

## Interactive decision (#451)

Leaving the TUI at its default `workspace-write` causes ordinary shell calls to
fail before execution in the pod. A model can then request escalation, opening
the CLI's approval machinery alongside the managed permission hook. Selecting
full access alone is insufficient: interactive `PreToolUse` previously left
policy questions to `PermissionRequest`, which full access need not raise.
Both the sandbox setting and approval routing therefore change together.

We rejected retaining `on-request` or `untrusted` approval: either can introduce
an additional native menu while AgentHub is waiting for the owner's answer.
We rejected retaining a terminal fallback after callback failure because the
single decision path must also hold when the backend is unavailable. We also
rejected treating `never` as auto approve: managed hooks still request and poll
AgentHub approval, recheck policy after approval, and deny invalid responses.
Policy reads and permission polls retry twice (one- and two-second backoff)
across a brief backend restart. Exhausted retries return an actionable denial
reason to the agent, visible in the Workspace tool result. Permission creation
POSTs are not retried: a lost response may already have created a card, and a
second POST would leave duplicate owner requests. The owner can ask the agent
to retry a tool after service recovery. No pod privileges or cluster security
settings change.

`tests/CodexInteractivePermissionsProof/proof.cjs` exercises the real pinned
0.166.0 TUI and managed hooks. Its matrix verifies shell execution, hard denial,
AgentHub approval and denial, delayed approval, and rejection of obsolete native
escalation requests. With the optional browser parameters, the real Workspace
Allow button resolves the waiting hook; execution is checked before and after
the click. Only the model and backend transports are synthetic.

Main reproduces the namespace failure. Immediate and delayed backend approvals,
and expired-card/native-menu approvals (card first or menu first) complete on
the tested main revision. Esc between answers interrupts the turn; the owner's exact both-prompts-answered
hang has not been reproduced. The regression asserts that the branch never
opens a native approval menu and that the result reaches the model without Esc.
This removes the competing path, without claiming an upstream TUI race fix.

This proof uses the real CLI, hooks, shell, and PTY, with synthetic model and
backend responses. It does not build the runtime image or exercise a deployed
backend/cluster; image smoke tests and CI cover the packaged runtime separately.
