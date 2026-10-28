# Switching agent providers within a session

Design spike for #345. This document proposes a direction; it adds no runtime or API
implementation. Switching means continuing the owner's work with another agent in the
same visible chat. Changing a model within one agent is a separate capability.

## What the source study establishes

An open-source agent client was inspected at revision
`d46463140776d3d45f0ce705a36e6b411f1d4b5d` on 2026-10-28. This was a source
review, not a running-client verification. No source was copied. Its relevant design is:

- An application thread, provider instance, provider session, native provider thread,
  turn and execution attempt have separate identities. The application history survives
  replacement of a native conversation or process.
- Adapters expose capabilities, session creation, thread creation/resume, turn start,
  interruption, approval responses, snapshots and optional history injection. They emit
  typed events for sessions, threads, turns, messages, tool items and runtime requests.
  An ingestor normalizes these into persisted domain events and projections. Command
  output deltas can be transient; final items carry the durable output.
- A transition policy distinguishes reuse, model change in the existing session,
  restart with compatible native resume, new thread with handover, and rejection.
  Continuation compatibility includes driver and continuation identity; sharing a model
  name does not establish compatibility. Missing adapter classification rejects a
  selection change rather than guessing.
- Cross-provider handover selects bounded historical messages and coverage information.
  A previously used target can receive the delta since it last saw the conversation;
  a new target gets full-thread coverage within a budget. Delivery records track target
  native thread, selected and omitted items, and pending/injected/inline status.
  Ambiguous injection requires a fresh native thread before retrying. This is context
  reconstruction, not translation of one provider's opaque resume files.
- Auth belongs to provider instances and credential bindings, separately from chat
  history. Auth controllers manage sign-in, completion, cancellation, logout and state
  subscriptions. Session admission is gated while a shared credential binding changes;
  related instances participate in that gate. A provider switch does not itself log in.

The inspected entry points were `ProviderAdapter`, `ProviderEventIngestor`,
`ProviderSessionTransitionPolicy`, `ProviderSwitchService`, `ContextHandoffService`,
`ContextHandoffDelivery`, `ProviderTurnStartService`, `ProviderAdapterRegistry` and the
provider auth-controller contract. These are research landmarks, not proposed dependencies.
The transferable lesson is to separate durable chat identity from replaceable execution
identity and make continuation an explicit, recoverable operation.

## AgentHub's starting point

[AgentPodSpecFactory](../../backend/Services/AgentPodSpecFactory.cs) selects a
provider-specific runtime image from the session's agent kind. Each image supplies its
own entrypoint, CLI, managed configuration and hooks.
[The driver contract](../../agent-runtime/common/driver-contract.js) already abstracts
command construction, readiness, transcripts, usage, attachments and credential installation;
it does not establish that multiple CLIs can safely share one running session agent.
[NativeTranscript](../../backend/Services/NativeTranscript.cs) reads provider-native
history, so a mixed-provider timeline needs provider identity per segment rather than
parsing all history using the session's latest agent kind.

[Context clear](../context-clear.md) provides the closest lifecycle precedent: reserve
input at a safe point, persist a bounded handover, start fresh native context, verify it
in the transcript, retain old history above a boundary, and report pending delivery.
Its current operation keeps the CLI and provider. Provider switching additionally replaces
execution, auth and possibly the pod. Reusing its handover discipline does not make its
existing endpoint a provider-switch endpoint.

## Runtime options

| Option | Benefits | Costs and failure modes |
| --- | --- | --- |
| New CLI in the same pod | Keeps checkout, uncommitted files, local services and filesystem; avoids pod scheduling delay | Current images do not promise all CLIs. A combined image or installer increases maintenance. Old child processes, credential files, environment and managed config can leak into the next provider. The session agent must swap drivers, transcript readers, hooks and usage sources without accepting stale events. |
| New pod for the same logical session | Retains provider-specific images, clean process tree and target-only credential projection; aligns with startup and pod identity checks | Scheduling delay; explicit workspace transfer; local services and sockets disappear. Requires durable switch state, execution generations and history segments. Concurrent source/target workspace writers must be prevented. |
| Linked successor session | Reuses more of current create/start and access checks; easiest isolated experiment | Changes session identity, links and peer routing; splits chat and accounting. Useful proof vehicle, but does not satisfy switching within one chat by itself. |

Recommend sequential replacement pods for the eventual feature. The isolation and existing
image boundaries outweigh startup latency. Reject a combined multi-CLI image as the first
step: it would couple runtime packaging and credential cleanup to an already complex
handover. Keep a linked successor as a possible experiment, explicitly labeled as such.
Do not run both agents against the same writable workspace during a switch.

## Proposed lifecycle and conversation model

Keep the public session/chat ID stable. Add an execution generation identifying provider,
account reference, pod identity and native conversation ID, with a history segment for each
generation. These are proposed concepts, not existing schema fields. Persist an owner
request ID and transition state: requested, preparing, source stopped, target starting,
handover pending, completed or failed. Serialize switches with stop, resume, delete,
context clear, credential changes and runtime updates under the session lifecycle lock.

1. An owner selects a supported target and supplies or reviews a short handover. Explain
   that this sends selected conversation content to the target provider. Check target
   availability, account access, budget, policy and workspace-transfer support before
   stopping the source. Do not put credentials in the handover.
2. Reserve terminal/composer and peer-message delivery at a completed-turn safe point.
   Refuse or wait while approvals, menus, owner drafts or background tool work remain.
   A forced interrupt is a separate owner decision with potentially unfinished work.
3. Persist the handover, source transcript cutoff and workspace checkpoint before
   acknowledging preparation. Include task, decisions, changed files, verification,
   pending work and unresolved questions. Bound its size, reject terminal controls and
   wrap it as text so a leading slash cannot become a command. Treat it as historical
   context, never as authority to expand tool permissions.
4. Stop and fence the old execution; preserve its final transcript and usage. Transfer
   the repository workspace, including untracked and uncommitted files, through an
   explicit manifest/checkpoint. A fresh clone alone loses work. Exclude provider homes,
   login files, caches and sockets. Existing archive/resume paths need an audit before
   reuse across providers; do not restore the source provider's entire home into the target.
5. Start a new native conversation in the target image with freshly resolved target
   credentials and policy. Gate readiness on the actual CLI. Deliver the saved handover
   before queued user or peer messages and confirm it in the target native transcript.
6. Publish a visible provider boundary with old/new provider, delivery state and omitted
   context. Commit the active generation only after confirmation; release queued input
   and finish the switch. Retain the prior history readably without replaying raw tool
   calls or opaque native state into the target.

The first version should use a bounded owner-reviewed handover, not automatic full-history
conversion. Full history increases disclosure, token cost and incompatible tool artifacts;
a summary can omit detail, so keep the old timeline available and state that limitation.
Returning to a previous provider should initially create fresh native context with another
handover. Native resume plus delta replay is a later optimization requiring its own proof.

Persist target native identity and delivery evidence. Recovery must reconcile the saved
phase with current pod identity and transcript before sending input. A timeout is not
proof that a paste failed. Do not claim exactly-once model execution: after ambiguous
consumption, block for recovery or replace the native conversation before retrying.
Never silently redirect queued messages back into the source conversation.

If target authentication or startup fails, keep the switch visibly failed/pending with
its handover saved. Retry uses the same request and checkpoint. Cancellation before source
stop can leave it running; after stop, resuming the old provider is an explicit recovery
operation with its own credentials and generation. Never let rollback produce two writers.
A pod failure after handover delivery resumes that generation rather than replaying the
handover into an unrelated conversation.

## Credential, policy and integration boundaries

Only the owner should initiate the first version; shared viewers, peer agents and descendant
callback tokens gain no switch authority. The hub checks session ownership and target
account access. Recheck them at target admission; client capabilities are not authorization.
Resolve only the selected account and permitted secrets for the target. Do not inherit old
provider environment variables or copy credential files. Honor refresh locks and revoked
or changing grants; missing target auth leaves a recoverable state for owner sign-in.

Retire the old execution's callback authority and reject its late hooks, transcript writes,
usage updates and approval responses by generation. Verify the current pod identity before
forwarding credentials or typed input; a stable public session ID must not authorize a
stale pod. Credential refresh routing must use the active provider/account generation,
not merely the logical session ID. Archive exclusions must cover staging files and backups.

Recompute effective policy from hub-side state: owner/admin agent allow lists, secret ceilings,
MCP grants and sharing hard denies, network constraints, approval mode and budget gates.
A handover cannot grant tools. Provider hooks differ; some enforce approvals per call while
others configure them at startup or have no equivalent hook. Initially reject target/mode
combinations without proven equivalent enforcement. Do not silently enable auto-approval to
make a switch succeed. Rebuild managed config, skills and MCP bindings for the target.

Keep usage attributed to the provider, account, billing classification and generation that
produced it; preserve old totals while checking admission for the new execution. Key native
turn/tool IDs by generation to avoid collisions. Reconnects must replay durable boundaries
and handover status. Composer attachments, slash menus, terminal sockets, pending approvals,
relay notifications and fleet delivery must follow the new execution; they cannot be assumed
portable just because the session ID remains stable.

## Recommended first step and evidence required

First agree a small execution-generation and handover contract, then build a separately
reviewed proof for two interactive providers using existing images and a bounded handover.
Do not add UI switching, autonomous runs, schedules or streaming-chat support until that
proof establishes lifecycle and policy equivalence. This spike changes documentation only.

The proof should run real pinned CLIs through the real session server, backend and pod
identity resolver, with synthetic accounts and a recording model. It must show:

- One visible chat retains both native histories; the target's first model request contains
  the handover, not source credentials, and queued messages arrive afterward.
- Dirty and untracked workspace files survive; no concurrent writer or source background
  process survives replacement; provider state and credential backups do not transfer.
- Crashes at each persisted phase, dropped/ambiguous input, missing auth, revoked access,
  scheduling failure and duplicate requests recover without losing the handover or sending
  queued input to the wrong generation.
- Stale pod callbacks and credential pushes fail; owner/shared/peer authorization, tool
  denials, MCP hard denies and budget gates hold across the transition.
- Reconnects, attachments, transcript segmentation, accounting and terminal routing use the
  right generation. Both directions work, including return to a previously used provider.

The source review supports these design choices; it does not prove AgentHub can switch
providers today. No real cross-provider pod transfer, auth transition, policy equivalence
or end-to-end switching was executed in this documentation-only spike. Workspace checkpoint
format and lifecycle-lock integration remain implementation design questions to resolve
before that proof, not assumptions to hide behind a provider selector.
