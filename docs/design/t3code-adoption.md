# T3 Code adoption evaluation

Evaluation for #454. Source snapshot: T3 Code `25b53299b75d4d8d1d0667ad47e9cfed3749776e`
(checked out from upstream HEAD on 2026-11-05), compared with AgentHub `873fab6c`.
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

## Evaluation status

Provider-by-provider authentication, capability comparison, licence inventory and proposed
follow-up scopes are being assembled against the pinned source above. This draft records the
architecture decision first so the evidence and recommendations remain recoverable.
