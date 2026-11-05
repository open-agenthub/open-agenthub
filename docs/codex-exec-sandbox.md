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
covers the permission hook's approval budget. Interactive callback errors and expired approvals also deny the tool. Keeping
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

## Approval wait budgets

Interactive Codex waits up to 30 minutes for an owner answer. Set
`AGENTHUB_CODEX_INTERACTIVE_APPROVAL_SECONDS` in the runtime environment to
shorten that window (1–1800 seconds; invalid values use 1800 and values outside
the range are clamped). Unattended sessions retain a 240-second window and
ignore that interactive setting. The elapsed-time deadline includes polling
and retry time; individual in-flight callbacks and final policy checks can add
bounded overhead. The legacy `AGENTHUB_APPROVAL_POLLS` override can shorten a
proof's poll count; it is not needed in normal deployments.

Both managed policy hooks use `timeout = 1860` (31 minutes), leaving room for
initial policy reads, creation, in-flight callback retries and the final policy
recheck. This is compatible with the backend's 35-minute stale-request sweep.
We rejected retaining the four-minute interactive budget: without a native menu
fallback, a person stepping away briefly would lose their chance to approve.
We cap the configurable interactive wait at 30 minutes to keep it below the
managed hook timeout and the backend safety-net expiry.

The pinned 0.166.0 source represents the hook timeout as `Option<u64>` in
`codex-rs/protocol/src/hooks.rs`; `codex-rs/hooks/src/engine/mod.rs` applies only
`.max(1)` and the command runner uses `Duration::from_secs(handler.timeout_sec)`
without an upper clamp. The real TUI proof loads and executes the shipped
1860-second setting successfully. No smaller maximum is imposed by those paths;
we do not claim to have waited the full 30 minutes in the proof.

An unanswered or backend-expired request tells the agent: "Nobody answered the
AgentHub approval in time; tool call denied." An unreachable or invalid approval
service instead reports "AgentHub approval unavailable; tool call denied."
Both reasons are visible in the Workspace tool result and invite a retry.

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
