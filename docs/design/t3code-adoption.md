# T3 Code adoption evaluation

Evaluation for #454. Source snapshot: T3 Code `25b53299b75d4d8d1d0667ad47e9cfed3749776e`
(checked out from upstream HEAD on 2026-11-05), compared with AgentHub `5f1c7005` (including the interactive Codex approval changes).
[Upstream source](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e).
This is a source evaluation, not a runtime certification or a commitment to ship the proposals.

## Decision

Adopt selected interactions and protocol ideas, not the upstream application or its server.
AgentHub already ports the activity-row geometry, Workspace arrangement, docked composer,
message actions and terminal drawer from `c77a7b7e`; see [the original decision](../agent-workspace.md).
That is a small UI subset, not adoption of its providers, model catalog or orchestration.

Current upstream has eleven registered provider drivers. A provider driver controls a harness;
a model is an account-dependent choice inside that harness. Copying a model list neither
installs the harness nor grants access to its service. AgentHub can already reach additional
models through OpenCode, OpenClaw or a compatible custom endpoint without adding an AgentKind.

Prefer capability and model discovery, structured OpenCode history, bounded conversation
handoffs and safer Git review workflows. Evaluate generic ACP in an isolated session runtime
before considering individual new harnesses. Embedding the upstream server would create a
second authority for sessions, credentials and permissions beside the existing backend.

## Method and scope

Read the checked-out implementations and their contracts, provider setup documentation,
dependency manifests and licence inputs. Provider registration is authoritative over README
marketing: some registered drivers are disabled by default. Presence in source does not prove
that an account, platform, subscription or remote installation works.

The provider table uses registration ordinals for integrations outside AgentHub's supported
product names. Ordinals refer to the ordered array in upstream
`apps/server/src/provider/builtInDrivers.ts`; they identify every driver without introducing
additional provider brands into this repository. The final evaluation links each ordinal to
its implementation. No upstream application code or assets are copied by this document.

Effort estimates are engineer-days for one experienced contributor, including implementation,
documentation and real-runtime proof, excluding procurement and external approval delays.
They are ranges, not additive delivery promises. **Has** means an equivalent capability exists;
**partial** means the user can do part of the workflow; **missing** means no integrated equivalent
was found. This evaluation changes no user-facing behavior, so it does not need a website feature
page; each accepted implementation will need its own website documentation.

## Provider and model comparison

All upstream execution below is on the selected server/environment host, even when controlled
from another device. AgentHub instead runs each agent in its session pod, with the selected
owner/account credentials, managed configuration and backend access checks. A host-wide login
or arbitrary executable setting must not become shared across AgentHub users.

| Driver in registration order | Current upstream execution and authentication | Model integration | AgentHub status and decision | Effort |
|---|---|---|---|---|
| 1: Codex | Local CLI app-server child over structured RPC. Existing CLI login/API configuration, shared/shadow homes, plus managed ChatGPT browser authorization and token broker. | Live app-server catalog, account-specific options, custom IDs, capability/version checks. | **Has** CLI subscription/API-key execution and multiple accounts; **partial** structured controls/catalog. Adopt discovery idea; do not copy the managed token broker or shared-home layout into pods. | 6–10 days for discovery; separate 10–20 day authentication review if ever requested. |
| 2: Claude | Agent SDK invokes local executable; uses normal CLI configuration/login or per-instance config directory and environment overrides for keys/endpoints. | SDK-reported models/capabilities with bundled/version-aware fallback, custom IDs, commands/skills and usage. | **Has** CLI login/key execution and account selection; **partial** streaming/catalog controls. Adopt capability discovery idea while keeping interactive PTY login and hooks. | 5–9 days, overlaps shared discovery. |
| 3: Cursor | V2 local-agent SDK, not its older ACP/CLI execution path. Per-instance SDK browser login produces stored credentials; configured API key overrides it. CLI/editor login is separate. | SDK catalog/options; auto maps to SDK default. No SDK endpoint override. | **Has** Cursor CLI login/API key; **partial** native conversation/events. Adopt structured-history idea only after a separate SDK feasibility proof. Reject immediate SDK replacement: interactive approvals, native fork/rollback and active steering are not all exposed. | 5–8 day spike; 15–25 day integration if approved. |
| 4 | Local CLI ACP child; CLI browser/device login or explicit API-key auth method. | CLI model command plus ACP model state and custom IDs; usage reader. | **Missing** native harness. Adopt idea through generic ACP first; dedicated image only on demonstrated demand and distribution review. Existing model gateways do not provide this harness. | 7–12 days after ACP foundation. |
| 5 | Local CLI `acp` child using host CLI login. Instances on one host share that login. | CLI status/default and models commands; custom IDs and effort. Backend selection stays in CLI, not the app. | **Missing** native harness. Adopt idea via ACP, defer dedicated port: no plan mode or text-generation helper; some subagent permission requests cannot be forwarded. | 6–10 days after ACP. |
| 6: OpenCode | Starts local server or connects to configured external server; supports separate 1.x/2.x paths. HTTP/SDK requests and event stream. Server password authenticates app-to-server; model-service login/keys remain OpenCode's own. | Connected provider/model catalog, model options, native plan agent, commands/skills and usage. | **Has** CLI and stored provider auth map, key/custom endpoint; **partial** native history. Adopt structured history/catalog idea first. Defer external-server mode: it needs explicit ownership and lifecycle rules beyond a URL/password. | 8–15 days for local structured bridge; 5–10 more for external server policy/proof. |
| 7 | Managed or manually installed ACP executable plus matching helper. Browser OAuth with remote callback handoff, enterprise/project auth, API key or application-default credentials depending on configured method; instance secrets stored separately. | Account-reported catalog, thinking choices and custom IDs, including multiple model families. | **Missing** runtime and auth methods. Not suitable for direct port now: large managed binaries, platform/install assumptions and new credential lifecycle. A separately packaged pod runtime is a future feasibility study, not a free model-provider import. | 5–10 day feasibility; 20–35 day integration if approved. |
| 8 | Local CLI ACP V3 engine with CLI auth; CLI login or account-plan-dependent API key. | Account model list/default, model-specific reasoning options and selectable agents. | **Missing** native harness. Adopt idea via ACP after Linux/non-root compatibility and credential-store proofs. Host login discovery is unsuitable in a multi-user backend. | 8–15 days after ACP. |
| 9 | Local coding-agent child with LF-delimited RPC, native session files and extensions; provider login in CLI or provider-key environment settings. | Agent-reported provider/model IDs and thinking levels; fallback default when discovery cannot complete. Model backends remain owned by that agent. | **Missing** native harness. Adopt idea only; a separate RPC adapter/image, trusted-extension policy and blocking tool-hook proof are required. It is not another generic ACP driver. | 12–20 days. |
| 10 | SDK spawns local MSP child. Host CLI login or explicit instance API key; ambient server key is intentionally removed. Status does not verify login. | Runtime model/options catalog; entries can still be inaccessible to the account. | **Missing** native harness. Defer dedicated port: separate protocol, no in-app login/install, no rewind or skill picker, extra maintenance for uncertain demand. | 12–20 days if prioritized. |
| 11: generic ACP registry/local command | Registry-managed binary/package or explicit local executable, direct argument spawn; ACP v2 preview with v1 negotiation. Agent-advertised browser/terminal auth or instance env keys, stored by agent. Multiple instances do not guarantee separate accounts. | Agent-reported catalog/options with custom-ID fallback; capabilities depend on agent. | **Missing** generic ACP runtime. Adopt idea, selectively port protocol tests/normalization only after licence review. Run in a session pod with approved/pinned artifacts, not arbitrary binaries on the hub. | 15–25 days foundation, excluding per-agent certification. |


Per-driver implementation references: [1](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/provider/Drivers/CodexDriver.ts), [2](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/provider/Drivers/ClaudeDriver.ts), [3](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-cursor/src/server/driver.ts), [4](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-grok/src/server/driver.ts), [5](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-fx/src/server/driver.ts), [6](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-opencode/src/server/driver.ts), [7](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/provider/Drivers/AntigravityDriver.ts), [8](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-kiro/src/server/driver.ts), [9](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-pi/src/server/driver.ts), [10](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-muse/src/server/driver.ts), [11](https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-acp-registry/src/server/driver.ts).

The [driver registry][u-drivers], [Codex adapter][u-codex], [Claude adapter][u-claude],
[Cursor implementation][u-cursor], [OpenCode implementation][u-openCode] and
[remaining driver implementations][u-packages] are the source basis. Ordinals 7 and 11 also use
[the managed ACP implementation][u-acp]. Upstream setup details are in [provider docs][u-user].
The companion table supplied privately to the coordinator maps ordinals to product names.

AgentHub's [AgentKind/auth contract](../../backend/Models/SessionModels.cs),
[runtime entrypoints](../../agent-runtime), [credential validator](../../backend/Services/ProviderCredentialValidator.cs)
and [account design](../provider-accounts.md) establish its five harnesses: Claude, Codex,
Cursor, OpenCode and OpenClaw. OpenClaw is an AgentHub capability without a dedicated upstream
driver in this registry; generic ACP availability would depend on a separately supplied adapter.
It runs its own CLI/configuration and auth profiles; API-key source is selected explicitly.
Do not remove it to match upstream.

### Model coverage is dynamic, not a list to transplant

The model-service integrations behind OpenCode, the multi-backend RPC agent (9), the managed
ACP runtime (7), the CLI backend selector (5), and registry agents belong to those harnesses.
They do not all become first-class model APIs in T3 Code. Authentication can be a subscription,
provider API key, delegated browser authorization or cloud/project credentials, depending on
the driver and account. A catalog entry is not proof of entitlement; refresh and selection errors
must retain the user's chosen model rather than silently switch billing or account.

AgentHub already supports three custom-endpoint protocols: Claude Messages, Codex Responses,
and OpenCode Chat Completions. The pod maps URL/model/optional key into each CLI's config and
probes protocol support. Cursor and OpenClaw reject this mode. See
[the endpoint implementation decision](../custom-model-endpoints.md) and
[the runtime mapping](../../agent-runtime/common/model-endpoint.js).
The missing piece is an account-aware browser catalog with reasoning/context/image/tool
capabilities, not universal model access. Native CLI `/model` remains available today.

Adopt discovery with short-lived, per-owner/account/endpoint caches and a manual model-ID escape
hatch. Show unavailable/stale/unknown states and protocol constraints. Do not copy upstream's
static fallback model manifest as an entitlement list. Do not relay all model services through
one backend credential or imply that the ACP registry grants a model subscription.

## Feature inventory and gap decisions

Rows group related surfaces rather than counting every menu item as a feature. Each **partial**
or **missing** row has a disposition and estimate. Estimates include authorization/error paths,
viewer behavior and documentation; existing-equivalent rows need no port.

| Upstream capability and source area | AgentHub comparison at pinned main | Disposition, reason and effort |
|---|---|---|
| Conversation timeline, compact tool log, copy/quote, docked composer, terminal drawer (`apps/web/src/components/chat`) | **Has** selected Vue ports, search, timestamps, CLI menus and native question notices. | Keep current ports and attribution; do not replace with a second React application. 0 days. |
| Structured text/reasoning/tool/plan streaming and durable replay (`orchestration-v2`, client-runtime) | **Partial** native Claude/Codex transcript following; legacy streaming Claude; other harnesses use terminal fallback. | Adopt event-normalization idea. Start with OpenCode bridge; preserve PTY and native history authority. 10–20 days shared foundation, plus adapters. |
| Provider/model/options picker, capability-driven permission modes, context meter, compaction (`provider-core`, `provider`, chat) | **Partial** runtime/model choices, CLI slash commands, auto-approve hooks, context-clear MCP; no equivalent unified live capability/context surface. | Adopt idea, not identical permission labels. Never advertise an approval callback or context count the CLI cannot provide. 6–12 days shared UI/contracts, plus probes above. |
| Plan review, persistent approvals/questions, attachment answers (`orchestration-v2`, permissions) | **Partial** structured questions and approvals plus terminal menus; provider differences already explicit. | Adopt idea for persistent structured plan/question records; retain CLI fallback and test reconnect/late-answer semantics. 7–12 days. |
| Attachments, paste/drop, rich files/Office previews, file search/editor (`workspace`, web files/search) | **Partial** uploads, Files previews/downloads, Office-to-PDF conversion, image composer and read-only diffs; no general source editor or identical interactive Office viewers. | Port isolated viewer algorithms only with notices; adopt idea for scoped file search/editor. Avoid arbitrary active document execution. 4–8 days viewers; 10–18 days editor/search. |
| Branch/base/head and per-commit/per-turn diff, review comments (`git`, `review`, `checkpointing`, web diffs) | **Partial** real read-only base/head Workspace diff with explicit file/byte/time limits. | Adopt idea for per-turn attribution and review-to-prompt. Extend existing pane rather than replace it. 8–14 days. |
| Checkpoint capture, workspace restore and conversation rewind (`Checkpoint*`, `checkpointing`) | **Missing** integrated turn checkpoints/restore. Session archives restore provider state, not atomic worktree+conversation checkpoints. | Adopt idea with explicit dirty/untracked/submodule scope, concurrent-writer checks, owner confirmation and recoverable restore. Reject blind Git reset/clean. 15–25 days. |
| Create/sync worktree branches and fork threads (`Worktree*`, thread management) | **Partial** repository clones and session duplication/children; no corresponding worktree lifecycle UI. | Adopt idea; pod-local worktrees and explicit shared-volume locking. Do not share one host checkout across owners. 10–18 days. |
| Change provider mid-thread, native/portable forks, bounded context handoff (`ProviderSwitchService`, `ContextHandoff*`) | **Partial** same-provider account switching, duplicate/child sessions, messaging and history reads. No equivalent cross-harness continuation. | Adopt bounded handoff idea with source references and omission disclosure. Do not copy reasoning, live tool state or credentials; new provider conversation remains distinct. 10–18 days after normalized history. |
| Commit text, branch/title generation, publish/PR creation, integrated PR review and source-host adapters (`textGeneration`, `sourceControl`, `pullRequest`) | **Partial** agents can use Git/CLI/MCP and stored Git credentials; no equivalent integrated review/publish surface across hosts. | Adopt idea, first one supported host behind existing credential scopes. Writes require explicit authorized actions; no new host-wide token authority. 12–22 days first integration; 4–8 each additional host. |
| Built-in delegation, native subagent projections and background-task lifecycle (`orchestration-v2`) | **Has** durable child sessions, fleet messages, priorities, wait and hierarchy; **partial** nested native CLI task visualization. | Adopt visualization idea only; retain distinct projected native tasks vs controllable child sessions. 6–10 days. |
| MCP tools, MCP apps, rendered HTML, browser previews and environment tools (`mcp`, `mcpApps`, `htmlRender`, `preview`) | **Partial** built-in/remote MCP, controlled browser, Files presentations; no full embedded MCP-app/HTML-render surface. | Adopt idea for separately sandboxed renderer, approved origin/capability bridge and viewer authorization. Do not embed tool HTML in conversation DOM. 10–18 days. |
| Secret requests and external secret-provider adapters (`secrets`, secret-provider packages) | **Has** owner secret request/grant/delivery; **missing** equivalent external-vault adapter. | Keep native grant flow; adopt optional resolver idea if demanded, with per-owner binding and audit. No host-global vault identity. 8–15 days. |
| Scheduled/background tasks (`scheduledTasks`, scheduling/background) | **Has** autonomous and scheduled sessions with UTC cron, concurrency policy and history. | Keep Kubernetes job authority; copying the local scheduler would duplicate execution. 0 days. |
| Thread sidebar, archive/pin/snooze, activity log and notifications (`sidebar`, notifications) | **Partial** session hierarchy, pins/organization, activity, pending messages, expiry and chat relays; no identical snooze/settlement workflow. | Adopt triage/snooze idea only if user demand warrants it. Keep session lifecycle separate from personal inbox state. 4–7 days. |
| Prompt links, reusable drafts, slash/skill discovery (`promptLinks`, composer, project settings) | **Partial** drafts, templates, skill library, native slash discovery and Claude prompt suggestion. | Adopt explicit reusable/deep-linked draft idea with preview and no automatic submission; reuse existing skill library. 4–8 days. |
| Usage/cost, account limits, diagnostics/resource telemetry (`usage`, diagnostics/resourceTelemetry) | **Has** usage/billing and resource controls, account-limit/failover services; **partial** unified context/status presentation. | Adopt per-capability availability labels and diagnostic UX; do not treat estimates as invoice truth. 4–7 days beyond catalog work. |
| Agent-led support triage (`triage`) | **Partial** startup diagnostics, logs and reusable proof runbooks, no integrated version-pinned triage launcher. | Adopt idea for opt-in redacted diagnostics plus matching release source. 4–7 days; posting remains a separate authorized action. |
| Multiple local/SSH/relay/cloud environments, guest links and remote control (`environment`, peer/relay/cloud, guest/auth) | **Partial** remote browser workspace, named/read-link sharing and scoped tokens; different pod execution/identity authority. | Not suitable as a wholesale port. Reuse guest-message moderation idea only if needed, layered on current sharing permissions. 6–10 days for that idea; no estimate for replacing execution model. |
| Native desktop/mobile apps, updates, background service (`apps/desktop`, `apps/mobile`, packaging) | **Partial** responsive web and browser pop-out; no native clients/OS service. | Not suitable for current adoption: introduces OS signing/update and native maintenance independent of server value. 40–80 days minimum new-client program if separately funded. |
| Desktop window capture/accessibility metadata, browser import (`snap-shot`, desktop/browser) | **Partial** browser screenshots/clipboard attachments, no host-desktop capture/import. | Not suitable for pod runtime; a separately installed local client would be needed. Do not copy OS permissions or browser profile data into server pods. 15–30 days only within a native-client program. |
| Device/simulator/emulator tools and remote device streams (`device`) | **Missing** device-farm integration; browser runtime is not a mobile emulator. | Not suitable now: host devices, platform SDKs and tool downloads require separate execution pools and budgets. 25–45 day exploratory program, not a normal provider port. |
| Enterprise/multi-user controls | AgentHub **has** backend user/session access, named shares, scoped credentials and edition gates; upstream local environment control is not an equivalent tenant boundary. | Retain AgentHub authority and CE/EE boundaries. Upstream MIT code does not make AgentHub EE code AGPL or authorize moving EE features into CE. 0 days. |

Upstream evidence: [server feature implementation tree][u-server],
[orchestration and handoffs][u-orchestration], [checkpoint store][u-checkpoints],
[web surfaces][u-web], [client synchronization][u-client], [desktop][u-desktop],
[mobile][u-mobile], and [user workflow documentation][u-user].
AgentHub evidence: [Workspace design](../agent-workspace.md),
[diff controller](../../backend/Controllers/WorkspaceDiffController.cs),
[diff collector](../../agent-runtime/common/workspace-diff.js),
[transcript design](../transcripts.md), [sharing](../session-sharing.md),
[credential scopes](../credential-scopes.md), [scheduling](../session-schedule.md),
[Office conversion](../../backend/Files/SessionFilePreviewWorker.cs),
[HTML download-only policy](../../backend/Files/SessionFilesController.cs),
[usage implementation](../../backend/Usage), [secret design](../session-secrets.md),
[backend controllers](../../backend/Controllers) and [enterprise sources](../../ee).

## Licence and distribution assessment

This is an engineering inventory and adoption gate, not a legal opinion or a claim that every
transitive/release-binary licence has been audited. No new code, dependency or asset is shipped
by this evaluation.

| Part inspected | Finding | Adoption consequence |
|---|---|---|
| Upstream root `LICENSE` and server package metadata | MIT; retention of copyright and permission notice required for copies/substantial portions. | Selected source ports are feasible into the existing AGPL application with notices. Keep exact legal attribution even where prose naming rules would otherwise omit it; seek owner handling if a new notice conflicts. Do not edit a notice to anonymize it. |
| Existing AgentHub port notice | `frontend/public/licenses/t3code.txt` exists; earlier UI decision pins `c77a7b7e`. | Keep it. A future port must record new source revision/files and reconcile current notice, rather than suggest old attribution covers all bundled parts. |
| Copied Office viewer and keyboard helper | Custom notice configuration identifies MIT adaptations independently of root licence. | Preserve their separate notices if their code is ported. UI concepts alone avoid dependency/asset copying. |
| Terminal renderer/native terminal framework and symbols font | Custom notices and checked-in local licence files identify MIT components; mobile terminal/font packaging also includes Apache-2.0 material. | Audit exact renderer/font files and any changed-file notices before distribution; do not inherit an entire native terminal stack for a layout port. |
| Notification and capture sounds | Custom notices identify CC0-1.0 source assets. | No need to copy for feature parity. Preserve provenance if adopted; root MIT is not their asset licence. |
| Optional device tools and mobile vendored code | Notice inputs include MIT, Apache-2.0, BSD-3-Clause and local vendor notices. Optional download is tracked separately from bundled use. | Review actual selected binary/source and release archive, including notices, patents and source obligations as applicable. Do not copy optional tools incidentally. |
| Native runtime helper notice | `scripts/licenses/libatomic.LICENSE` states GPL-3.0-or-later WITH GCC-exception-3.1. | The exception is conditional, not an MIT relicence. Packaging/linkage and distribution/source duties need review if this helper is carried over. No binary adoption proposed. |
| Package dependencies and SDKs | Server manifest includes provider SDKs, native modules and shared packages; lockfile pins resolution. Notice generator traverses installed dependency metadata with overrides and custom notices. | Root MIT does not cover these packages automatically. Generate an exact dependency/notice report from the selected build, inspect unresolved/dual licences and SDK terms before porting an adapter. No blanket SDK clearance asserted. |
| Installed provider executables/ACP downloads | Downloaded or user-installed harnesses, helper archives and model services have their own distribution/use terms. Registry checksums authenticate bytes only when supplied. | Prefer pinned images and explicit supported artifacts. Authentication implementation is not permission to redistribute a runtime or use a subscription through any client. Review each proposed image separately. |
| Reference repositories and branding | Checkout includes reference source beyond shipped packages; logos/marketing/native app identities are unnecessary to parity. | Exclude reference repositories and branding from adoption. A source checkout is not a release bill of materials. |

Licence evidence: [root licence][u-license], [custom/override notice inventory][u-notices],
[notice generator][u-notice-generator], [native helper notice][u-native-notice],
[server dependency manifest][u-manifest], [lockfile][u-lock] and
[existing AgentHub notice](../../frontend/public/licenses/t3code.txt).
The manifest generator's existence is evidence of a process, not proof its output covers every
release. Dependencies were not installed and no production release archive was rebuilt here;
therefore a complete binary/transitive licence clearance remains explicitly unverified.

## Proposed follow-ups, in order

These are proposed issue scopes, not created issues or agreed roadmap items.

1. **Account-aware model and capability discovery** (6–12 days shared foundation plus provider probes):
   expose supported controls and stale/unknown states; manual ID fallback; prove account switch,
   custom endpoint, no-entitlement error and secret-free viewer reads against real pinned CLIs.
2. **Structured OpenCode conversation bridge** (8–15 days): retain CLI/PTY and managed policy,
   read native messages/tools with stable ordering, reconnect and bounded paging; prove real
   server/CLI history and backend sharing access, rather than a mock transcript alone.
3. **Pod-scoped ACP feasibility and contract** (15–25 days): one approved agent, v1/v2 negotiation,
   native auth, pinned install, credentials excluded from ordinary archives, cancellation,
   questions/approvals, MCP access and live-pod identity. No arbitrary hub-host execution.
4. **Turn-scoped diff review** (8–14 days): extend existing read-only pane, preserve truncation
   notices; prove edits vs merge-introduced changes, untracked/binary files and concurrent writers.
5. **Recoverable checkpoints and explicit restore** (15–25 days): first write scope/ownership
   decision; prove dirty/untracked work, failures mid-restore, linked worktrees and concurrent turns.
6. **Bounded cross-provider handoff** (10–18 days after history foundation): distinguish native
   resume from new conversation; preserve user instruction text and source references; show
   omissions; prove model-window limits and incomplete/interrupted turns with real harnesses.
7. **Managed worktree lifecycle** (10–18 days): isolated paths, locks, cleanup, child-session
   association and restart behavior; prove shared volume and archive/restore cases.
8. **Integrated review/publish for one supported source host** (12–22 days): existing scoped
   credentials, explicit write actions, base selection and review-to-prompt; prove real Git and
   API permission/error behavior before broadening hosts.

The remaining table rows are optional later ideas, not dependencies for these eight. Do not sum
all provider estimates and present that as a plan to adopt “most” of upstream: overlap is substantial,
while native clients and device execution are separate products. The highest-value first slice
is discovery plus OpenCode history; the largest reusable expansion is ACP after isolation proof.

## Verification and remaining uncertainty

The evaluation was checked against an actual upstream clone at the full SHA above and AgentHub's
pinned main checkout, not screenshots or a README-only feature comparison. The repository proof
script verifies source references, registry count/order, and the notice inputs used here; its
README records how to repeat it. It is a source-evidence check, not an execution test.

No model was called, no provider login performed, no paid plan tested, no native app built,
no device connected and no released binary licence manifest certified. No product fix is
proposed, so runtime regression tests would not validate this written comparison. Each follow-up
above names the real-runtime proof that must precede its implementation push. Estimates may
change with CLI/ACP versions, account restrictions, platform support or licensing results.

[u-drivers]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/provider/builtInDrivers.ts
[u-codex]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/orchestration-v2/Adapters/CodexAdapterV2.ts
[u-claude]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/orchestration-v2/Adapters/ClaudeAdapterV2.ts
[u-cursor]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-cursor/src/server
[u-openCode]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/provider-opencode/src/server
[u-packages]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages
[u-acp]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/provider/acp
[u-user]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/docs/user
[u-server]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src
[u-orchestration]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/orchestration-v2
[u-checkpoints]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/src/checkpointing
[u-web]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/web/src/components
[u-client]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/packages/client-runtime/src
[u-desktop]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/desktop
[u-mobile]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/mobile
[u-license]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/LICENSE
[u-notices]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/third-party-licenses.config.json
[u-notice-generator]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/scripts/lib/third-party-licenses.ts
[u-native-notice]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/scripts/licenses/libatomic.LICENSE
[u-manifest]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/apps/server/package.json
[u-lock]: https://github.com/pingdotgg/t3code/tree/25b53299b75d4d8d1d0667ad47e9cfed3749776e/pnpm-lock.yaml
