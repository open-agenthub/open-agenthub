# AutonomousPolicyProof

Checks what an unattended runtime hook actually does against the backend's **real** policy
decision. A canned callback is not enough: the #160 proof answered `allow` for any input, while
`AgentPolicyMatcher` hard-denied every command with a redirect, so autonomous Codex and OpenCode
sessions could run almost nothing (#244).

`Program.cs` is a stand-in for the session callback routes:

- `/agent-policy` runs `AgentPolicyMatcher.Decide` with an empty session policy.
- `/permission` answers like `InternalController.RequestPermission` for an auto-approving session.

Every request is logged as one JSON line.

```bash
export DOTNET_ROOT=<sdk dir>            # DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 without libicu
dotnet build -c Release -o /path/out tests/AutonomousPolicyProof
/path/out/AutonomousPolicyProof 47245 callbacks.jsonl true    # autoApprove on
/path/out/AutonomousPolicyProof 47246 callbacks.jsonl false   # owner policy is the boundary
```

The scripts next to it drive the real runtime code against that stand-in:

| Script | What runs | Needs |
|---|---|---|
| `codex-exec-smoke.js` | pinned Codex in `exec` mode with the driver's arguments and managed `requirements.toml` bound over `/etc/codex`: redirect, plain, variable, git commit, `apply_patch` edit | `CODEX_BIN`, `PROOT_BIN` (static proot), `POLICY_URL` |
| `opencode-gate-smoke.mjs` | OpenCode's `policy-gate.mjs` for the same shell commands | `POLICY_URL` |
| `deny-forms-smoke.mjs` | eight wrappings of an unallowed `git push` through Codex's `policy-hook.js` and the OpenCode gate | `POLICY_URL` of an `autoApprove=false` stand-in |

Codex offers `apply_patch` only for a model slug it knows, so the mock provider is called with
`gpt-5.5`. Results as of #244, Codex 0.160.0, are listed below.

- The Codex and OpenCode smokes against the matcher before #244: the redirect and `$HOME`
  commands were BLOCKED. Afterwards all of them run.
- `deny-forms-smoke.mjs` with auto-approve off: all eight forms are denied by both hooks.
