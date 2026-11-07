# Provider switching implementation plan

Plan for #466 and reopened #345, based on `provider-switching.md` and source at
39293baa (1.18.0). This milestone changes no product code. Implementation awaits
owner confirmation of step 1. Estimates are engineering days, including proofs and
review fixes; steps are sequential and total 13–18 days.

## Current contracts and gaps

- `AgentPodSpecFactory` creates `/workspace` as `emptyDir`, not a persistent claim.
  Replacing a pod today loses its dirty and untracked workspace. A fresh clone or
  provider home archive cannot meet the requested same-volume guarantee.
- `KubernetesSessionService.GetConversationAsync` reads one session transcript key;
  `NativeTranscript.Parse` recognizes native record shapes. `SessionTranscripts.PageAsync`
  selects live entries instead of stored entries when live data exists. Merely changing
  the session agent field would therefore hide the previous execution's conversation.
- #462 projects OpenCode native SQLite messages into the existing JSONL path. Reuse
  that projection and its ordering/fallback identity; do not introduce another native
  history reader. Existing source identities can reset browser paging on a projection
  change, but are not a durable cross-provider execution ledger.
- Provider state archives already select a provider-specific key. Transcripts and
  scrollback are session-scoped, and returning to a provider could overwrite its old
  archive. Add generation-scoped artifacts rather than relying on provider alone.
- Context clear supplies input reservation, safe-point detection, bounded text and
  transcript-confirmed delivery. Its retries intentionally tolerate delivery into old
  context; cross-provider recovery must instead prevent ambiguous replay.
- Runtime updates and lifecycle methods already use a shared runtime-update lock.
  Extend that database-backed exclusion and audit every lifecycle mutation; a new
  in-memory mutex would not serialize multiple backend replicas.

## Target contract

Keep the public session ID, owner, project, shares, links and conversation endpoint.
Persist execution records with generation, agent, credential reference, native identity,
pod UID, artifact references, final transcript cutoff and usage attribution. Qualify
native message/tool IDs with the generation. The active execution is separate from
the target being prepared. Add an idempotent owner request and durable transition
phases: preparing, source stopped, target starting, handover pending, completed, failed.
Store failure reason and delivery evidence. Authenticate internal operations against
the execution generation, not just the public session ID.

The conversation path composes sealed earlier segments, a provider boundary and the
current segment. Fetch current live data only for the current segment; its absence
falls back to that segment's stored copy, never replaces the whole timeline. Bound
pages and memory. Carry a composite history revision so projection changes reset
paging without inventing a conversation clear. Plain-text API/MCP and shared readers
use this same composition after their existing access checks. Old session artifacts
remain readable through a lazy generation-zero adapter without rewriting native files.
A segment is as complete as the retained native/projection data: previously truncated
history cannot be reconstructed and must not be advertised as complete.

For switching-enabled sessions, mount one session-owned PVC at `/workspace`, with
unchanged repository paths, Git directories, dirty files and untracked files. The
source pod must be confirmed deleted before the target mounts it; RWO is not a
single-writer fence by itself. Reject detach uncertainty and overlapping starts.
Provider homes and generated managed config stay on separate ephemeral storage;
restore only the active generation's native archive on an ordinary same-provider
resume. Cross-provider starts, including returning to a previous provider, always
start a fresh native conversation. Retain prior archives for that execution's recovery,
never translate or restore them into another execution.

Initial switching availability requires persistent workspace support and configured
storage class/RBAC/quota. Existing emptyDir sessions cannot switch until a separately
verified migration copies their workspace with an integrity manifest and final quiescent
checkpoint. Do not pretend existing sessions already have a reusable volume. Refuse
that migration in the first switching release; the UI explains why and offers creating
a switching-enabled session. This limitation needs owner agreement before step 2.

## Handover and failure behavior

The owner reviews the exact text before source stop: task, instructions still relevant,
decisions, repository paths, source references, checks, pending work and questions.
Use an initial 16,000-character total envelope, including fixed framing and omission
metadata, matching the existing context-clear bound. This is a character budget, not
a promise to fit every model window: preflight target capacity, reserve output space,
and reject unknown/insufficient capacity. Never silently drop selected instructions;
ask the owner to shorten the text. Retain references and report inaccessible references.

Always include an explicit omission note: earlier native messages, raw tool outputs,
attachments, hidden reasoning and resume files are not transferred unless included
as selected plain text. Record available/selected/omitted ranges or counts where known;
label unknown coverage rather than inventing counts. The visible historical timeline
remains readable but does not become target model context. Handover grants no tools.
Reject control bytes and frame leading slash text as ordinary input.

At a completed-turn safe point reserve composer, terminal, fleet and relay delivery.
Refuse active turns, drafts, menus, approvals and known background work in the initial
release; no force-interrupt button. Persist source cutoff and handover, flush final
history/state/usage with acknowledgement, fence old callbacks, delete the source and
wait for deletion. If final persistence cannot be established, do not proceed.

Resolve target account access and credentials both before source stop and before
admission. No source credential files, provider environment or home cross over.
Regenerate managed policy, MCP bindings, skills and secret projections from hub state.
Preserve effective approval mode, tool restrictions, MCP sharing hard denies, network
policy and budgets. Initial supported directions are interactive Claude and Codex;
other providers require their own policy and real-CLI proofs. OpenCode history remains
supported and covered by foundation regression proofs; switching it is later scope.

Start the target, verify actual CLI readiness, send handover first and confirm its
marker/content in the target native transcript. Only then activate the generation and
release queued messages. Terminal sockets reconnect to the new pod. Previously issued
approvals cannot be answered into the target. Reject late hooks, writes, usage and
credential pushes from the old generation; generation-scoped upload destinations
cannot overwrite the current one. Preserve historical provider/account usage totals.

A duplicate request returns its saved state. Missing auth, startup or scheduling failure
leaves a visible recoverable transition with its handover and workspace intact. Before
source stop cancellation leaves the source running. Afterwards recovery needs explicit
owner action and a single admitted generation. Reconcile native transcript identity and
delivery evidence after restart before replay. Ambiguous consumption blocks automatic
retry; an explicit fresh target conversation is required if evidence cannot settle it.
Do not claim exactly-once model execution or automatically roll back to the source.

## Three shippable steps

### 1. Execution history foundation and bounded handover preview — 4–5 days

One PR for #466, referencing #345, with no runtime provider selector or pod replacement.
Add durable execution/segment records, generation-zero compatibility and generation
artifact references. Compose stored/live histories through the existing conversation
path, preserving boundaries and bounded paging. Add an owner-only handover preview
contract with explicit omissions, validation and capacity result; preview performs no
CLI input. Keep target provider admission disabled until step 2. Include a decision
record and a separate user website documentation PR describing history/preview and
limits if a preview is exposed in Workspace.

Proof scripts under `tests/ProviderSwitchHistoryProof/` run real pinned Claude, Codex
and OpenCode through real `common/server.js`, deterministic recording model endpoints,
native transcripts/stores and the real backend with Postgres/artifact storage. Capture
two independently executed provider segments under one logical history through the
new internal persistence contract; this is a history proof, not a pod-switch claim.
Prove paging beyond one page, stored/live merge, backend restart, reconnect, reused
native IDs across generations, OpenCode fallback/source resets, existing context-clear
boundaries, shared viewer access/revocation and API/MCP parity. Verify preview bounds,
instructions/references retention, explicit omissions and owner-only authorization.
No generated proof outputs in Git. A green foundation is independently deployable.

### 2. Owner-only sequential runtime switch — 6–8 days

One PR for #345 referencing #466. Add opt-in PVC workspace lifecycle and chart settings,
owner API/UI, durable transition orchestration, target account selection and admission,
generation fencing, final flush, pod replacement and transcript-confirmed handover.
Add user website documentation in a separate PR, linked to both issue and product PR.
Refuse existing emptyDir sessions, unsupported modes/providers and unsafe transitions.
No native resume across providers; no autonomous/scheduled or streaming-chat switches.

Proof uses real pinned CLIs and session server, real backend/Postgres/object storage,
and real Kubernetes API plus kubelet/volume lifecycle. Show both directions and return,
byte-identical dirty/untracked multi-repo content on the same claim, no overlapping
writers/background process, old callback rejection, target-only credentials, unchanged
shares, policy/budget enforcement, handover in the first target model request and
queued input afterwards. If only an API stand-in is available, keep the PR draft until
a real volume mount/deletion proof runs; API objects alone do not prove persistence.

### 3. Recovery and fault acceptance — 3–5 days

One follow-up PR for #345, with the selector remaining gated until acceptance passes.
Exercise crashes at every saved phase, backend replica races, unavailable storage,
missing/revoked credentials, target startup failure, failed final persistence, duplicate
requests, dropped input, ambiguous handover, interrupted source turn and near/exceeded
model capacity. Prove recovery after completed delivery resumes that generation without
replaying the handover. Verify terminal, attachments, sharing, approvals, fleet/relay
routing, usage and archive lifecycle after reconnect and deletion. Document recovery
choices for owners. Enable only the combinations proved. Provider expansion and
emptyDir migration are separate estimates after this acceptance gate.

Step 2 ships disabled by default; step 3 is the enablement gate. The two issue PRs are
sequenced rather than concurrently modifying lifecycle code. The owner may instead
request the first two steps combined, at the cost of a larger review surface.

## Rejected alternatives and scope

Reject a combined multi-CLI image: it couples packaging and credential cleanup.
Reject successor public sessions: links, shares and one visible chat would split.
Reject recloning or source-home transfer as workspace persistence: dirty files vanish
or provider-private state crosses the boundary. Reject full-history/tool replay and
native file translation: incompatible semantics and uncontrolled context disclosure.
Reject automatic rollback and blind retries after timeout: both can execute twice.
Reject a global rewrite of native history into a new event system in step 1: #462 and
the existing native projections already supply the needed history source.

No runtime behavior or real-CLI proof is claimed by this planning milestone. Owner
confirmation is required before implementing step 1, particularly its foundation-only
scope, Claude/Codex-first switching and refusal of existing emptyDir session migration.
