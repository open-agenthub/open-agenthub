# Provider switching with repository-backed emptyDir workspaces

Phase 1 design for #550, based on the 1.21–1.23 releases, `ProviderReplacement.cs`
and the switching history, implementation, native-resume and recovery decisions.
This document changes no runtime behavior. The owner approved implementation on 2026-11-08 with the ignored-file exception below.
Acceptance still requires the real no-PVC proofs. The previous PVC-only admission decision is superseded
only after the emptyDir proofs pass; PVC remains an optional workspace strategy.

## Decision: code in Git, execution state in object storage

An emptyDir switch requires every configured repository to be clean and its exact HEAD
commit available through a recorded remote branch. AgentHub never commits, pushes or
uploads workspace contents implicitly. Existing generation-scoped native archives,
transcripts, usage and the reviewed handover remain the execution recovery mechanism.
The repository manifest is durable transition metadata, not a copy of repository files.

Reject bundling Git objects, patches, dirty files or untracked files into the archive.
Those can include credentials and ignored secrets, bypass current provider-state archive
exclusions, and make a bounded execution checkpoint an unbounded workspace backup.
Reject an owner-approved discard button in this release: accepting data loss is too easy
and does not establish a recoverable source. Reject requiring a PVC or migration to one
(#522) as the normal prerequisite: most clusters cannot provision one. Migration can
remain separate optional work; this issue does not implement it.

Keep a separate operator switch-admission setting, default off until acceptance, independent
of `agent.workspaceVolume.persistent`. The latter continues to choose storage for new
sessions only. An operator can enable the proved emptyDir path without provisioning claims;
existing supported emptyDir sessions can use it after their runtime is updated. Do not
silently convert a session between storage strategies. Existing PVC switches retain their
same-volume contract, including dirty and untracked files.

## Source inspection and owner review

Inspect all configured repositories at their existing deterministic workspace destinations.
Use Git argument arrays, bounded output and deadlines; never interpolate paths, branch names
or URLs into shell code. Refuse missing checkouts, corrupt metadata, conflicting paths,
symlink escapes, linked worktrees whose Git directory is outside the checkout, extra nested
repositories, submodules and large-file pointer workflows in the first implementation.
These layouts need separate restore proofs rather than a misleading clean-tree result.

For each repository show its configured identity, local branch, HEAD, remote branch,
uncommitted tracked change count, untracked count and unpushed commit count when known.
Show unknown comparison as unknown and refuse, never report it as zero. Block staged and unstaged tracked changes and non-ignored untracked files. Ignored files
do not block: show their count and a bounded sample per repository as “Ignored files will
not carry over”, covered by the same owner acknowledgement as files outside repositories.
The new agent must recreate dependencies (for example, run `npm install`) and build outputs;
`.env` and similar ignored files do not carry over.
Never put file contents, credential-bearing remote URLs or raw Git stderr into UI or logs.
Names and counts are owner-only, escaped text, not handover instructions.

Require a configured remote, an attached local branch and a remotely available exact commit.
Missing remote, unavailable fetch credentials, detached HEAD and unpushed commits each produce
an actionable per-repository refusal. Read-only repositories are ready when their recorded
commit is fetchable. Write permission is deliberately not a switch precondition: restoring
code needs only read access, and dry-run pushes cannot establish server-side write authority.
If the agent's requested push fails, its commit stays unpublished and the next check blocks.

Fetch the selected remote branch into a private temporary verification repository using
the session's selected Git credentials, with hooks and local configuration disabled.
Verify the exact local HEAD is reachable from that branch, rather than trusting a stale
remote-tracking ref or a local `ahead=0`. Record the branch tip observed as well as HEAD;
a later tip advance does not change the selected commit. No remote mutation is performed.
Before stopping the source, independently verify that the credentials to be projected into
the new pod can fetch that same branch and commit. Provider account credentials do not
replace repository credentials. Revoke or unavailable access blocks before source stop.

If any repository is unsafe, the switch dialog lists what is missing per repository and
offers **Ask the agent to commit and push** plus **Check again**. The first action sends an
owner-reviewed prepared message to the still-running source through ordinary input delivery;
it is not switch approval and does not run Git in the backend. Propose a fresh task branch
such as `work/session-<short-id>` with collision handling. The message asks the agent to
review changes for secrets, commit intended work, remove or relocate remaining transient
files, and push without force. Never suggest committing or pushing directly to main or the
remote default branch without explicit owner confirmation through the normal approval flow.
The owner can edit the message or fix repositories manually. Recheck after the source turn
finishes and require a new handover review; no automatic switch after the agent replies.

List bounded relative names of files outside configured repositories under **Will not carry
over**, with total counts and a visible omission notice when the list is truncated. Do not
read contents or follow symlinks. Require acknowledgement bound to the review. EmptyDir
files outside repos, caches and installed tools are not preserved. Refuse a session with no
configured repository in this first scope; it has no durable code source.

## Bounded manifest and security

Persist a versioned manifest containing storage strategy, configured repository index,
normalized relative destination, local branch, selected remote ref, exact commit ID and
observed remote tip. Refer to configured URLs and credential selections by existing IDs;
never store embedded credentials, credential helpers or local remote configuration.
Initial limits: 32 repositories, 64 KiB total serialized manifest, 1 KiB per relative path
or branch/ref, 200 displayed outside-file names and 16 KiB total displayed names. Reject
overflow rather than silently skipping a repository. Cap Git diagnostic capture at 8 KiB,
status enumeration at 10,000 entries, and each network check at 60 seconds; exceeding a
bound yields an actionable refusal. These are new metadata/check limits, not a claim that
existing native archive sizes are newly bounded. Do not enlarge existing archive contents,
size policy or exclusions. Keep the existing 16,000-character handover limit.

Retain provider-state auth-file and atomic staging-file exclusions unchanged. Never add
`/workspace`, `.git`, bundles or patches to the native archive. A clean commit can still
contain secrets already published by its owner; cleanliness is not secret detection.
The target can read repository content with the selected repository permissions. The UI
must say this without claiming that switching provides a secret boundary. Untracked paths
may themselves be sensitive, so show them only to the owner and do not copy them to public
history, audit text, model input or shared readers.

## Restore and handover order

Bind the manifest digest, source pod UID/generation, storage strategy, outside-file
acknowledgement and exact handover to the existing review digest. Reserve all input at the
completed-turn safe point and recheck cleanliness and remote availability before preparation
is acknowledged. Recheck the frozen checkout after graceful CLI stop, before deleting its
pod; any changed commit, branch, file status or manifest requires a held refusal. Do not
silently replace the reviewed manifest. Cancellation after stop remains prohibited under
#530, so explain the held recovery action rather than inviting an unsafe edit/review.

1. Save the source history, native checkpoint and final usage with acknowledgement, alongside
   the durable manifest. Keep the existing lifecycle lock and request-id idempotency.
2. Fence old callbacks and wait for the exact source pod UID to be deleted. An emptyDir
   needs no claim-consumer wait, but absence of a claim is never proof the old pod is gone.
   Keep helper/browser shutdown and the single-writer fence.
3. Clone each configured remote into the same absolute destination in the target emptyDir.
   Fetch the recorded branch, verify the recorded commit remains reachable, and create a
   local branch with the recorded local branch name at that exact commit, with the selected
   remote branch as upstream. Never leave detached HEAD or substitute the current branch tip.
   Refuse invalid ref names or a disappeared/unreachable commit. The source must use a safe
   task branch before review if continuing on its current branch would violate branch rules.
4. Verify every destination/HEAD/branch against the manifest before launching the native CLI.
   Regenerate managed configuration, policy, skills and secret projections from hub state.
   Cross-provider startup uses fresh target native state and target-only account credentials;
   it never restores the source home into the target. The source archive remains available
   only for that execution's recovery. Ordinary same-provider recovery restores its matching
   native archive after restoring code; preserve #530 strict native identity validation.
5. Deliver exactly the reviewed handover as the first target user turn, confirm it in the
   native transcript, checkpoint and commit the execution generation, then release queued
   messages. Returning to a previous provider still starts fresh native context.

The durable current execution must retain its repository manifest beyond the transition,
so completed-generation pod replacement/resume also checks out the recorded code before
native resume. Later uncommitted or unpushed work in an emptyDir is still ephemeral: archive
saves do not back it up. An ordinary restart must not claim to recover that work, advance the
recorded commit without verification, or replay the original task/handover. A later switch
records a new clean/pushed manifest. More general workspace checkpointing is separate work.

## What carries over

| State | emptyDir contract |
| --- | --- |
| Code | Exact committed remote-backed trees, local branch names and repository destinations. No local Git config, hooks, reflogs, stashes or unpublished branches. |
| Visible history and access | Existing retained generation history, session ID, shares, links and owner/Collaborator/Viewer roles; existing history bounds still apply. |
| Model context | Only the reviewed handover on a switch; same-provider recovery uses its own valid native checkpoint. Visible history is not full model context. |
| Settings and authority | Selected Git access, policies, approval mode, MCP restrictions, network rules, budgets and selected secrets regenerated from hub state. Previous approvals do not transfer. |
| Not preserved | Dirty tracked and non-ignored untracked repository files (refused before switching), ignored repository files (acknowledged loss), files outside repos (acknowledged loss), processes, memory, sockets, installations and caches. Attachments keep their existing storage/access rules; references do not inline their bytes. |

## Failure and recovery

Reuse the #530 journal phases, lifecycle lease, callback/generation fences, native identity
checkpoint and no-ambiguous-replay rules. Add a typed repository workspace identity rather
than putting a synthetic claim UID into PVC fields; old pending PVC operations retain their
existing interpretation. Persist manifests before the source can be deleted, and validate
schema/version/digest on every reconciliation. Missing or malformed identity holds.

Before stop: dirty files, failed fetch, missing permissions or archive failure leave the
source available; release a reservation only through acknowledged cancellation/handback.
A source lost before its frozen manifest/checkpoint is acknowledged holds: never infer
that its latest code was pushed. After stop but before deletion: preserve the surviving
pod on validation failure. After deletion: a Git outage, removed commit, revoked credential,
clone failure or scheduling problem holds with the manifest and queue intact; fix access or
restore the recorded remote commit, then retry the same operation. Do not choose a newer
commit, resurrect a source automatically or silently create another target after an
ambiguous create. Unknown source/target identity still requires operator diagnosis.

An init-container clone retry is safe only before native launch in the same admitted pod.
Once native execution may have started, use the #530 target identity/transcript/checkpoint
reconciliation; absence does not authorize another handover. Completed-generation recovery
requires both fetchable recorded code and its matching native archive. A missing native
archive refuses before launch; repo files and Workspace history cannot reconstruct it.
Repository hosting is now part of recovery availability; remote branch deletion/rewrites
can make recovery impossible. No claim of exactly-once model execution, general node-loss
acceptance or backup durability follows from this design.

## UI text and acceptance gate

Replace the PVC-only rejection and “create a new persistent-workspace session” guidance.
Available emptyDir sessions explain: “Code is restored from the commits shown below. Save
and push all repository work before switching. Files outside these repositories and running
processes will not carry over. The new agent receives only your reviewed handover.” Show
per-repo branch/commit and checks, outside-file acknowledgement, storage failures and repair
actions. Keep PVC-specific text for its same-volume behavior. Keep owner-only switching,
existing supported packaged non-root interactive Claude/Codex restrictions, starting-state
polling, capacity/account/budget checks and held-operation controls.

Phase 2 must extend real k3s proofs with no PVC and assert zero claim creation, real
Claude→Codex→Claude, multi-repo exact commit/branch restore despite remote tip movement,
Git credentials sufficient to fetch, old callback rejection, first handover once, queued
input ordering, retained history/roles and strict completed native resumes. Reproduce dirty,
staged, non-ignored untracked, unpublished, missing remote, ref deletion, permission
revocation, clone outage, size overflow and changed-review refusals. Verify ignored files permit switching only with acknowledgement and disappear after restore.
Inspect actual archives for planted workspace-secret exclusion. Extend the #530 backend/container/pod fault and
held-recovery cases for repository identities, including failures after source deletion and
before native launch. Retain PVC regression acceptance; do not infer it from emptyDir tests.

Keep proof scripts and README under `tests/<Name>Proof/`; generated logs/results/screenshots
stay outside Git. Record exact heads, counts and stand-ins in the PR. No real provider
login, billing or production service calls. Publish user instructions, limits, roles and
edition applicability in a separate website PR after implementation; this decision document
is not user documentation. Mark the product PR ready only after required proofs, CI and
review-thread resolution. This Phase 1 draft supplies no implementation or acceptance claim.
