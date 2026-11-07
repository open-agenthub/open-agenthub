# Provider replacement recovery and acceptance

Step 3 follows #487 and #490, with #529 tracking recovery acceptance for #345/#466.
The owner approved this step on 2026-11-07. The merged implementation is the baseline;
this initial plan adds no runtime behavior. EmptyDir migration remains separate in #522.
Estimate: 3–5 engineering days including real-worker proofs and review corrections.

## Recovery contract

Persist one owner request, reviewed handover, source generation, workspace claim UID and
source/target pod identities. A backend restart reconciles that operation. A runtime crash
is different: the container can restart within the same pod UID, so pod identity alone
cannot establish whether native input has already executed. Test both identities.

A passing scenario either completes with one native handover and one active writer, or
holds durably with the workspace and queue preserved and an actionable owner explanation.
A timeout does not authorize replay. Returning to the source automatically was rejected:
its authority and usage cutoff may already be retired. Native files are never translated.

| Persisted boundary | Recovery evidence and expected result |
| --- | --- |
| requested | Same source generation/pod, no competing approvals or helpers; reservation can be reconciled or cancelled. |
| prepared | Reviewed text and frozen source history persist; recheck target credentials/capacity before source stop. |
| stopping, before/after stop acknowledgement | Reconcile the same native stop and final archive/usage; do not force-stop or infer success from a timeout. |
| source-stopped | No claim consumer remains; target admission and callback rotation happen once under the execution fence. |
| target-admitted | Durable target generation exists; only this operation may create its runtime. |
| target-starting | Reconcile an existing annotated pod; an absent ambiguous target never permits an automatic fresh prompt. |
| target-started | Match native identity and the exact first user turn; checkpoint before committing history. A restarted container cannot replay the startup prompt. |
| committing | Advance history once; reconcile an already committed matching native generation. |
| releasing | Release the matching target and queued input without opening another generation. |
| cancelling | Wake only the still-identical source generation and retain maintenance fencing through acknowledged handback. |
| completed | Ordinary restart/resume uses the completed native generation/archive and never sends the handover again. |

Also fault between each external side effect and its journal acknowledgement. Reopening
only database store objects does not prove backend process death, native CLI restart or
Kubernetes volume behavior.

## Proof design

Extend `tests/ProviderSwitchProof/` and the real PostgreSQL/backend HTTP proofs. Keep fault
barriers outside production authorization: isolated database triggers, response proxies and
actual Kubernetes actions can stop a specific operation at a known boundary. Reject a public
fault-injection route or a production bypass flag. Keep proof scripts and README in the
branch; generated native histories, logs, images and result files stay outside Git.

The combined rig uses the branch backend, PostgreSQL, object storage, real k3s kubelet/PVC,
shipped entrypoints/hooks and pinned Claude/Codex interactive CLIs. Model/account services
remain synthetic recording fixtures with TLS verification enabled. Explicitly separate
subscription transport acceptance from real sign-in, entitlement and refresh.

Acceptance covers backend death/restart, two replicas, same-pod container restart, source
and target pod loss, missing/changed claim, scheduling failure, archive/usage failure,
missing/revoked target credentials, duplicate requests, dropped replies, ambiguous handover,
interrupted turns, capacity/budget/policy refusal and completed-generation native resume.
Check unchanged dirty/untracked repositories and Git metadata, first handover count, native
identity, one writer, old callback rejection, queue generation, attachments/terminal routing,
shared history/roles, approval and MCP hard denies, usage cutoffs and cleanup.

## Enablement gate

Keep the existing default-off storage/admission behavior while the matrix is incomplete.
The final change must state its storage-default effect explicitly and enable only the
proved packaged non-root owner Claude/Codex interactive-terminal/PVC combinations. Existing
emptyDir sessions keep their unavailable reason. Provider expansion, migration and cloud
snapshot automation remain separate work.

Publish owner recovery choices, retained-state limits and remaining unverified environments
in a separate website PR. A failed ambiguous case is a held operation, not a passing recovery
claim. Do not claim exactly-once model execution or enable a combination whose fault boundary
has not been exercised. The coordinator confirmed that `agent.workspaceVolume.persistent` remains false;
enablement releases the proved combinations only when the operator opts in. A different
operator PVC provisioning default needs a separate owner decision;
acceptance alone must not silently change storage allocation for existing installations.

## First reproduced fault: missing native state on completed resume

The real Codex negative control started a fresh thread and sent the original source prompt
when resume state was absent. The legacy fallback was valid for old single-provider sessions
but loses the completed replacement generation. Require native restoration on resumed
nonzero generations, suppress the original source prompt, refuse missing storage before
deleting a surviving pod or changing status, and prevent fresh fallback if native resume
fails. Runtime-update checks must use the same generation-specific archive as the pod.

Retain legacy generation-zero behavior. Reject falling back to the latest provider archive:
a return to the same provider can have several independently retained generations. Reject
a fresh startup with the old task as recovery: neither visible history nor workspace files
reconstruct the native conversation or authorize silently re-running that task.

## Native identity selection within an archive

A real Codex archive can contain more than one CLI-created conversation. The recovery proof
creates a second real native conversation and changes the archive pointer; the baseline then
selects that other thread. A strict completed-generation resume now seeds the pointer from
the registry's committed native identity before discovery. If that rollout is missing, it
refuses to discover a different recent rollout. Explicit native context changes can still
update the active process marker after startup. The original missing-state fix is shipped
separately by #533; this identity binding and the wider fault matrix stay in #529/#530.
