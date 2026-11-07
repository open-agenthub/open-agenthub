# Execution history and handover preview foundation

Decision for step 1 of #466, building on #345 and #462. Runtime replacement remains
outside this change. `provider-switching-implementation.md` records the approved sequence.

## One history, replaceable execution

Keep the session as the access-control identity. `session_executions` records the agent,
native identity, account reference and archive/transcript references for each generation.
A unique partial index permits one unsealed execution. Sealing a generation and inserting
its successor is one transaction under the session row lock, with expected-generation
comparison. Failed/racing advances roll back. Only an internal lifecycle contract may
advance; no API, callback or peer tool can create a new generation in this release.

Existing sessions adapt lazily as generation zero. Their existing object names and upload
URLs stay valid. New execution references include the generation so returning to an agent
cannot overwrite its earlier archive. A generation-scoped database transcript snapshot
supports deployments without object storage. Late saves to a sealed generation are rejected.
The current product upload endpoints still write generation zero: switching admission,
callback/presigned upload fencing and new pod credentials belong to step 2 before any
runtime can create another generation. Native resume files are not rewritten or translated.

Seal normalized native entries, rather than translating native resumes into a new provider's
format. #462 remains the OpenCode authority and exporter. Sealed entries have generation
and agent provenance; native IDs can repeat across generations. Keep the old context-clear
boundaries, and add an execution boundary without claiming context was natively resumed.

Stored reads compose the sealed prefix with the current generation's stored projection.
Live reads replace only that final projection, not the prefix. The active source identity
participates in the composite conversation revision even when the first visible entry is
old. Reconnect/source changes therefore cannot reuse cursors against different projections.
The existing Workspace renderer already renders boundary text. API/MCP plain-text consumers
read the same stored composition; existing shared readers retain access checks and masking.

Single-execution sessions (generation zero) return native entries unchanged and retain
existing full-history paging, including histories over these combined-view limits.
Only the multi-execution combined view is bounded to 5,000 entries and about one million characters. It explicitly
marks omitted earlier entries; database rows and native archives are not removed by that
view limit. A seal over the bound is refused, rather than silently discarding instructions.
Step 2's checkpoint builder must select an explicitly marked bounded final segment before
sealing an oversized source. This foundation does not promise unlimited history or recover
records already absent from a native projection. Session deletion cascades ledger/entry rows;
execution artifact paths stay under the existing session prefix cleanup.

Rejected: one latest-provider transcript key, which would replace earlier history; native
file translation, which confuses context with readback; a new universal provider event
stream, which duplicates the existing projections; and unbounded history materialization,
which lets a long conversation exhaust a backend replica. Persistent archive references
are groundwork, not evidence of a cross-provider runtime or mounted workspace transfer.

## Preview has no execution authority

The owner-only web API accepts selected instructions, task, decisions, pending work and
references. Required instructions/task are never truncated. The complete framed envelope
is capped at 16,000 characters and rejects terminal controls. References are retained but
not opened, eliminating an unintended fetch or attachment-disclosure path. A fixed header
keeps leading slash text from being interpreted as a CLI command on later delivery.

The omission note always names native history, raw tools, attachments, hidden reasoning
and resume files. Coverage and target capacity remain unknown. The result always says
switching is unavailable; it does not log in, resolve credentials, contact a model, queue
input, change approvals or send the selection. Character count alone cannot establish
model capacity. Reject unsupported targets and noninteractive/streaming-chat modes.

Rejected: accepting a caller-provided context window as verified admission; silently
clipping instructions; automatically selecting all historical messages; and showing a
provider selector before runtime transition and recovery have been proved.

Owner-approved follow-up constraints: persistent workspace storage is behind a chart flag,
default off until step 3 passes. Document storage class, size and deletion cleanup then.
Existing ephemeral sessions must explain that switching is unavailable. Migration is a
separate ticket, not hidden in this foundation. No PVC is created by step 1.

## Proof

`tests/ProviderSwitchHistoryProof` captures real pinned Claude and Codex through the real
session PTY server and reuses the real OpenCode native-store proof. A real backend host
seals the independently captured histories into one logical history through the internal
contract. It checks owner/link/named-viewer paging, stored/live composition, provenance,
masking and revocation, preview authorization/bounds and restart readback. Authentication,
model replies and cluster discovery are stand-ins. It does not prove pod replacement,
workspace persistence, target model capacity, real provider sign-in or switch recovery.
Generated logs/results remain outside Git. Database tests additionally exercise concurrent
advances, owner scoping, stale snapshots, return generations and cascading deletion.
