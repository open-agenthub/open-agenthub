# Repository checkpoint proof

Run `node tests/ProviderRepositoryProof/inspect.mjs`. It creates actual Git repositories,
a read-only HTTP remote, real clones and local changes, and runs the production inspection
module. Ignored files must not block and their contents must not enter the result.
The other controls exercise saved-commit and checkout refusals. Everything is temporary;
no credentials, model services or production endpoints are used.

This is a repository-inspection proof only. It does not establish target credential
projection, pod replacement, native CLI handover or k3s recovery. Those remain the
combined no-PVC acceptance gate. Generated evidence is never committed.

The real PostgreSQL journal test `RepositoryIdentitySurvivesAdmissionAndNeedsFinalCodeVerification`
checks typed identity, final verification fencing and generation-scoped manifest recovery.
`RepositoryCloneRestoresRecordedAncestorOnNamedBranch` runs the factory's actual init shell
against real Git, including a moved remote tip and a differently named local branch.

For the combined proof dispatch `install-smoke.yml` on the feature branch with
`prove_provider_emptydir=true`. This reuses the native provider fixture with persistent
workspace allocation disabled, and asserts exact commits/branches, ignored/outside file
omission and no session claims through Claude → Codex → Claude in both synthetic credential
modes. A dispatched workflow must pass before this counts as native/kubelet acceptance.

`menu.cjs` checks the actual merged Workspace entry through the session's `⋯` menu at
1280 and 390 px. Run `tests/SessionHeaderProof/fixture.cjs` on localhost:8080 and the frontend
Vite server on a browser-reachable origin, then set `PROOF_CDP`, `PROOF_URL` and `PROOF_OUT`
when invoking `node tests/ProviderRepositoryProof/menu.cjs`. It overrides only the owner
provider-switch API responses with explicit repository fixtures. It verifies menu closure,
repository checks, omission acknowledgement, exact review binding and viewport bounds.
This proves UI integration, not backend or native authorization. Screenshots stay in Files.

The inspection proof observes an ambient native-state TMPDIR while the HTTP Git fetch is
in flight. It must remain empty throughout, so a concurrent native archive cannot capture
Git credential or object scratch. `PROOF_CHECKPOINT_MODULE=/absolute/baseline.cjs` runs the
same controls against an extracted baseline module without editing/stashing the worktree.
The afb7e74e module fails this live-location control; the 3f7ab22b module passes.
