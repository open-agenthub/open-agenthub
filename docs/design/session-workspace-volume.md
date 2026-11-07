# Persistent workspace for provider replacement

Step 2 decision for #345, after the execution-history foundation in #487. This design
milestone adds no product code. Runtime switching remains disabled until its lifecycle,
policy and recovery proofs pass. See `provider-switching-implementation.md` for the full
sequence and `provider-switching-history.md` for the existing history contract.

## Provisioning and identity

Use `agent.workspaceVolume.persistent`, default false. Enabling it provisions one
filesystem PVC for each newly created, supported interactive Terminal Claude/Codex
session. Persist claim name, UID, requested class/size and bootstrap state on the session.
A chart change must never silently convert an existing ephemeral session or replace an
existing claim with emptyDir. Turning the flag off disables new claims and switch
admission; existing claims still serve ordinary pause/resume/delete operations.

Proposed defaults are `workspaceVolume.size: 10Gi` and `storageClass: ""`. An empty class
omits storageClassName and uses the cluster default, matching existing chart conventions.
An explicit class selects that class. Validate a positive storage quantity, retain the
admitted values per session, and report Pending/class/quota failures. Initial provisioning
uses ReadWriteOnce. There is no automatic resize or class migration. Settings affect new
sessions; changing them does not move existing data.

Create the claim in the session namespace with session/owner/component labels. Record its
UID and verify identity before attachment or deletion. Backend RBAC needs namespace-scoped
claim get/list/create/delete, not cluster-wide PV/snapshot authority. Do not give the PVC
an ownerReference to a replaceable pod: pod deletion is a normal operation, not a request
to delete the workspace.

Rejected: a cluster-wide shared volume or claim selected by a user-supplied name, which
would couple session access boundaries; and relying on a stable claim name alone, which
could attach a different volume recreated under that name.

## Repository and process lifecycle

Mount the claim only at `/workspace`. Preserve existing repository paths, Git metadata,
tracked changes, untracked files and other workspace files across pod replacement. Provider
homes, temporary directories, credentials and generated managed/MCP configuration remain
separate ephemeral mounts. Provider-native state is restored from only that execution's
archive, never from another provider's home.

Initial bootstrap needs a recorded repository-layout manifest and an atomic completion
checkpoint. Existing init cloning assumes an empty directory and removes failed clone
destinations. Reuse must skip clone/fetch/reset/clean entirely and validate the expected
layout. A missing/inconsistent bootstrap checkpoint or unexpected destination must refuse
startup without deleting existing files. Partial initial clones need staging and explicit
recovery; a retry must not mistake a user's existing checkout for disposable partial data.
Refuse repository-layout changes until they have a separately safe lifecycle.

Pause, runtime update, failure and provider replacement retain the claim. Serialize starts,
switches, stop/delete, credential changes and context-clear admission using the shared
session lifecycle lock. Reserve input and flush/seal source transcript, usage and native
archive before stopping it. Retire source callback/generation authority, request graceful
pod deletion and confirm that no live or terminating workspace consumer remains before
creating the target. Use actual pod and claim UIDs. Never force-detach after timeout.
ReadWriteOnce does not itself prevent two writers on one node.

The same logical session and shares survive. A target gets fresh native context, a reviewed
bounded handover and freshly resolved target-only credentials. Returning to a prior provider
also starts fresh native context. Native resume is for restarting the same execution,
not for joining incompatible provider contexts. Unsupported modes, providers and unsafe
states remain refused. Existing emptyDir sessions show “switching not available for this
session” with a reason; migration is separate scope.

## Deletion and failure recovery

Use the existing central session-deletion path for manual deletion, expiry and account
cleanup. First stop and confirm all producers are gone, then delete only the recorded,
owned claim and wait for PVC deletion before removing the session registry row. Keep that
row when cleanup fails so retry preserves claim ownership and location. A failed switch
or pause does not delete the claim. Create failures likewise retain enough metadata to
retry or explicitly delete the session without orphaning a claim.

PVC deletion is not proof of physical storage erasure. The storage class reclaim policy
controls the PV/backing data: Retain can require operator cleanup. Do not delete PVs,
external backups or snapshots from this feature. Do not silently adopt a replacement claim
or create a blank one when a recorded claim is lost.

## Backup and native state archives

The PVC provides workspace persistence, not backup. Existing archives remain provider-native
state only, scoped by execution generation. Do not add workspace files to those archives or
restore a source provider archive into the target. A same-execution restart requires its
own native state plus the recorded workspace claim. A missing claim is an explicit recovery
failure, not permission to reconstruct a fresh clone from an archive.

Operator backup/restore must cover the database/ledger, quiesced workspace volume contents
and object-storage artifacts at a consistent checkpoint. Quiesce all writers; a filesystem
snapshot while an agent edits does not promise a coherent application checkpoint. Existing
state-archive uploads alone do not back up dirty repositories. Automated snapshot APIs,
cross-cluster restore and emptyDir migration are separate scope. Storage class, size,
retention/deletion behavior and these backup limits need a separate website documentation
PR before release.

## Proof gate

Prove real mounts and sequential pod replacement against a real kubelet/storage provisioner,
including byte-identical dirty/untracked multi-repository files, Git metadata, failed
bootstrap, claim UID mismatch, deletion timeout, pause/resume and cleanup retry. Then run
real pinned Claude/Codex through the real transport/backend in both switch directions,
including return, handover ordering, target-only credentials and stale-generation rejection.
A standalone API server or fake Running pod cannot prove volume persistence or process death.
Keep default-off gating through step 3 fault/recovery acceptance.
