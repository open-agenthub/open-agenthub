# Codex Agent Support Design

**Date:** 2026-07-19  
**Status:** Approved design  
**Scope:** Open AgentHub backend, frontend, agent runtimes, Helm chart, local Docker Desktop Kubernetes workflow, and documentation

## Objective

Add Codex as a first-class session agent alongside Claude. A user can select Claude or Codex for Interactive, Autonomous, and Scheduled sessions and independently choose subscription authentication or API-key billing for each session. Subscription credentials persist per user across pods, including refreshed tokens, without exposing credential values through the public API.

The implementation uses separate Claude and Codex runtime images. They share only provider-neutral terminal transport and persistence code; CLI installation, authentication, state, resume, MCP setup, hooks, and command construction remain provider-specific.

## Product behavior

### Session choices

Every session has:

- `agent`: `Claude` or `Codex`;
- `authMode`: `Subscription` or `ApiKey` for new sessions;
- `mode`: the existing `Interactive`, `Autonomous`, or `Scheduled` value.

The New Session UI defaults to `Claude` and `Subscription`. Agent and authentication selectors are independent. Duplicate and resume retain both values. Editing either value affects the next start or resume because it can change the runtime image and mounted credentials.

Existing records migrate to `Claude` plus an internal `Auto` authentication mode. `Auto` preserves the current Claude behavior and is visible only when editing a migrated session. New sessions cannot select it. Once an existing session is explicitly changed to `Subscription` or `ApiKey`, it no longer uses `Auto`.

Session list, search, detail, and history views display the selected agent. Search includes the agent and authentication labels without displaying secret status or values.

### Authentication behavior

The pod receives only the credential selected for that session:

| Agent | Auth mode | Pod credential |
| --- | --- | --- |
| Claude | Subscription | writable copy of the user's Claude credential file |
| Claude | API key | `ANTHROPIC_API_KEY` from the general user credential Secret |
| Codex | Subscription | writable copy of the user's Codex `auth.json` under `CODEX_HOME` |
| Codex | API key | `CODEX_API_KEY` only for the Codex process invocation |
| Claude | Auto | current compatibility behavior for migrated sessions |

Subscription mode never injects the provider API key. API-key mode never mounts the subscription Secret. This reduces credential exposure and makes the selected billing source deterministic.

The Credentials screen adds a write-only OpenAI API-key field beside the Anthropic API-key field. Credential status responses expose booleans only. Empty fields retain the stored value, while the existing explicit clear mechanism can remove it.

An Interactive subscription session may start without a stored login. Claude uses its normal terminal login. Codex uses its headless-compatible device-code login (`codex login --device-auth`). When the provider credential file appears or changes after a refresh, a background watcher uploads it through the per-session internal callback. The backend validates JSON, expected top-level shape, and a fixed size limit before replacing the provider-specific user Secret. Invalid uploads never replace a previously valid credential.

Autonomous and Scheduled sessions perform an authentication preflight. If their selected credential is unavailable, the session fails before starting the provider CLI and records a clear, non-secret diagnostic in scrollback and status.

For Codex API-key sessions, `CODEX_API_KEY` is scoped to the `codex exec` process, matching the documented non-interactive CLI contract. Interactive Codex API-key sessions create an ephemeral file-based login by piping the key to `codex login --with-api-key` at startup; that file is not uploaded to the subscription Secret.

OpenAI documents that Codex stores file-based credentials in `$CODEX_HOME/auth.json`, refreshes ChatGPT sign-in tokens during active use, supports device-code authentication for headless systems, and supports `CODEX_API_KEY` for `codex exec`. The implementation pins file-based storage in the container instead of relying on an unavailable OS keyring. See [Authentication](https://learn.chatgpt.com/docs/auth) and [Non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode).

## Runtime architecture

### Images

Build and publish two agent images:

- `agent-runtime-claude`, containing the Claude CLI and Claude driver;
- `agent-runtime-codex`, containing the Codex CLI and Codex driver.

Each image includes the same provider-neutral Node runtime for:

- PTY lifecycle;
- shared terminal WebSocket and replay buffer;
- per-connection shell terminal;
- status and notification callbacks;
- scrollback backup;
- periodic and final state persistence;
- signal handling.

Each image supplies a provider driver with a narrow interface:

- build an Interactive command;
- build an Autonomous or Scheduled command;
- build a resume command;
- report the provider state directory;
- prepare and watch the selected authentication source;
- render MCP configuration;
- install provider hooks and policy files;
- recognize a failed resume that should fall back once to a fresh session.

Shared code must not contain provider CLI flags or provider credential paths. Provider code must not implement WebSocket or persistence transport.

### Custom images

When no custom image is selected, the backend uses the default image for the selected agent. When a custom image is selected, the init container uses the selected provider runtime image and copies its common runtime, provider driver, Node binary/modules, provider CLI, entrypoint, and hooks into the shared runtime volume. The session container then launches that copied entrypoint.

Custom-image requirements remain glibc, bash, git, and curl. Documentation describes that the injected CLI now follows the selected agent rather than always injecting Claude.

### Backend and Helm configuration

Backend options and Helm values expose separate Claude and Codex runtime image names. Registry, tag, pull policy, pull Secret, runtime class, resource limits, and custom-image policy remain shared unless a future requirement needs provider-specific overrides.

Development scripts build four images: backend, frontend, Claude runtime, and Codex runtime. CI image workflows build and publish both runtime images. Plain Kubernetes manifests are updated consistently with the Helm chart.

## Persistence and resume

The session record stores `Agent` and `AuthMode`. The existing provider-specific conversation identifier becomes an agent conversation identifier in application code and schema naming. The migration preserves existing values and does not recreate the sessions table.

New provider state objects use distinct S3 names:

- Claude: `claude-state.tgz`;
- Codex: `codex-state.tgz`.

Existing Claude state keys remain readable. State archives exclude credential files because subscription credentials have their own user-scoped Secret and must not be restored from older session state. Authentication restore always occurs after state restore so a stale archive cannot shadow a newer user credential.

Claude retains its explicit session-ID resume behavior. Codex state is isolated per AgentHub session and resumes only from the restored state for that session. If Codex cannot accept a caller-selected conversation ID, the driver uses the restored Codex home and resumes its only/latest thread. The driver may fall back once to a fresh thread when state is absent or invalid, then reports subsequent failures normally.

Scheduled runs keep the existing scheduling semantics. Each CronJob pod uses the session's selected provider, authentication mode, policy, and MCP configuration. It does not silently switch billing sources when the selected credential is missing.

## Command and tool policy

### Policy model

Autonomous and Scheduled sessions store an agent policy with:

- allowed built-in tool names or patterns;
- allowed MCP tool names or patterns;
- allowed shell command prefixes;
- a default decision of deny.

Legacy Claude `AllowedTools` data is preserved and translated into the new structure when a migrated session is edited or duplicated. The public API continues accepting the legacy field during a compatibility window, but new frontend requests send the structured policy.

The UI shows provider-aware examples. Claude examples use native names such as `Read`, `Edit`, and `Bash(git*)`. Codex examples distinguish built-in tools from command prefixes such as `git status`, `npm test`, and `dotnet test`. Empty policy sections do not mean allow everything.

### Enforcement

Claude Autonomous/Scheduled runs continue to use the native allowed-tools mechanism, augmented by the existing AgentHub hooks for live MCP-sharing policy.

Codex Autonomous/Scheduled runs use `codex exec --sandbox workspace-write` with non-interactive approvals. A trusted runtime-owned `PreToolUse` hook evaluates shell, local function, and MCP calls against the AgentHub policy. It is installed in the user/system Codex layer, not the checked-out repository, and automation starts Codex with the supported hook-trust bypass for this vetted image-owned hook.

Shell input is normalized into argument prefixes. Linear compound commands are evaluated component-by-component. Commands using substitutions, redirections, variable expansion, globs, or control flow that cannot be safely decomposed are denied unless an explicit wrapper prefix allows the complete invocation. A denied action returns a concise reason to Codex and is written to scrollback without prompt or secret contents.

Interactive sessions keep their normal provider approval flow and the existing out-of-band Slack approval path. Codex hooks adapt their event JSON to the same internal permission endpoint. Session-wide MCP restrictions created by sharing remain authoritative for both agents and are checked before an interactive approval can allow the call.

Hooks are an additional guardrail, not the isolation boundary. Pod security context, RBAC, resource limits, namespace separation, and NetworkPolicies remain mandatory. OpenAI's hook documentation notes that specialized tool paths can opt out of the default hook path, so the product documentation must not describe the allowlist as a complete container sandbox. See [Hooks](https://learn.chatgpt.com/docs/hooks) and [Rules](https://learn.chatgpt.com/docs/agent-configuration/rules).

## MCP translation

AgentHub retains the existing `.mcp.json`-shaped JSON as its public session configuration format. The Claude image consumes it directly. The Codex image translates supported server entries into its private `config.toml` before starting Codex.

The initial Codex translator supports:

- stdio servers with command, argument, and environment fields;
- streamable HTTP servers with URL, bearer-token environment reference, and environment-backed headers;
- enabled/disabled state and supported tool allow/deny lists.

Unknown fields that can be safely ignored produce a diagnostic warning. Missing required fields, unsupported transports, or ambiguous secret-bearing inline headers fail validation before the provider starts. Raw MCP config remains stored as today so editing and duplication are lossless.

## Notifications and collaboration

The common transport continues to clear `question_pending` when a user reconnects or sends input. Claude retains its notification hook. Codex uses lifecycle events available from its runtime-owned hooks to report that an interactive turn stopped and is waiting. Both providers use the existing callback token and internal-only backend route.

Owner, Collaborator, and Viewer behavior does not change. A collaborator with terminal input can influence the running agent, so sharing documentation continues to treat a shared writable session as access to the selected session's capabilities. Mounting only the selected credential limits exposure but does not make a writable shared session a separate trust boundary.

## Secret handling

Use separate per-user Kubernetes Secrets for Claude subscription credentials, Codex subscription credentials, and general API/Git credentials. Secret names sanitize the owner identically to current behavior. Pods have no Kubernetes service-account token and cannot read other Secrets through the Kubernetes API.

Provider credential uploads:

1. authenticate with the session callback token;
2. resolve the owner from the stored session, never from request data;
3. enforce the session's provider and subscription authentication mode;
4. enforce content type, size, JSON validity, and provider-specific structure;
5. atomically create or replace only the matching owner Secret;
6. avoid logging credential bodies, token fragments, or parsed identity claims.

Kubernetes Secret values are base64-encoded, not inherently encrypted at rest. Production documentation recommends Kubernetes encryption at rest and restricts backend RBAC to the session namespace. Local development uses synthetic credentials for persistence tests.

## Error handling

- Missing selected credentials fail Autonomous/Scheduled before CLI launch.
- Interactive subscription sessions without credentials enter login rather than failing.
- Invalid refreshed credentials never replace the previous valid Secret.
- MCP conversion failures prevent a partially configured start.
- Policy denials state which policy category rejected the action without echoing sensitive input.
- Runtime image pull and custom-image bootstrap errors remain visible through Kubernetes phase/reason and session history.
- Resume falls back to fresh at most once.
- Provider CLI exit codes map to the existing `Succeeded` and `Failed` states.
- Callback or S3 failure never prints presigned URLs or callback tokens.

## Testing strategy

Implementation follows red-green-refactor. Every production behavior is introduced by a failing focused test.

### Backend tests

Cover:

- enum serialization and migration defaults;
- duplicate/edit/resume retention of agent and auth mode;
- credential status, merge, clear, JSON validation, and provider-specific Secret storage;
- exact pod/CronJob image, volume, mount, and environment selection for every provider/auth combination;
- absence of unselected credentials;
- custom-image init runtime selection;
- provider state S3 keys and legacy Claude state compatibility;
- structured policy validation and legacy conversion;
- MCP validation failures and safe diagnostics.

Pod construction should be extracted behind pure builders where necessary so tests assert generated Kubernetes objects without a live cluster.

### Runtime tests

Cover:

- common transport behavior independently of either provider;
- exact fresh/resume CLI commands for every mode;
- subscription restore precedence and API-key scoping;
- watcher upload on create/change/refresh and no upload in API-key mode;
- invalid/failed upload retry without token logging;
- Codex MCP conversion fixtures;
- allow/deny behavior for built-ins, MCP tools, plain commands, compound commands, and unsafe shell syntax;
- one-time resume fallback;
- Dockerfiles containing the correct CLI and provider files only.

No real subscription token or API key is used in automated tests.

### Frontend tests

Cover:

- agent and authentication selectors and defaults;
- provider-aware permission fields and examples;
- API payloads for create, edit, and duplicate;
- credential status and OpenAI API-key write/clear behavior;
- legacy `Auto` display only for migrated sessions;
- agent labels in list, detail, history, and search.

### Build and chart tests

Run backend tests, frontend tests/build, runtime tests, both runtime Docker builds, `helm lint`, and rendered-template assertions for image configuration and Secrets. CI must build both runtime images before the feature is considered complete.

## Docker Desktop Kubernetes acceptance

Use the existing guarded setup scripts, which refuse any kubectl context other than `docker-desktop`.

1. Build backend, frontend, Claude runtime, and Codex runtime with the local tag.
2. Deploy the development Helm values with both runtime images and wait for rollout health.
3. Create Claude and Codex Interactive and Autonomous sessions plus a Codex Scheduled session.
4. Inspect generated Pod/CronJob specs to prove only the selected credential is present.
5. Exercise subscription persistence with synthetic auth fixtures and prove updated files reach only the correct owner/provider Secret.
6. Exercise API-key preflight, allowed and denied commands, MCP conversion, pause/resume, terminal reconnect, and status transitions.
7. Start an unauthenticated Interactive Codex session and verify that it reaches device-code login.
8. Do not copy a developer workstation's Codex credentials into the cluster. A real subscription login is completed only by the user in the session terminal.

The acceptance report records commands, relevant non-secret output, image digests/tags, test counts, and any environment limitation.

## Documentation and release compatibility

Update README architecture, feature list, credentials setup, custom-image behavior, persistence, assumptions, Docker build examples, security notes, and local Kubernetes instructions. Helm values document both runtime images and Kubernetes Secret encryption-at-rest expectations.

The API remains backward compatible for existing session records and legacy `allowedTools` requests. Existing Claude-only installations receive defaults that preserve the current image configuration. Release notes call out the additional Codex runtime image so private registries mirror it before enabling Codex sessions.

## Out of scope

- A generic third-party agent plugin framework;
- copying local desktop Codex credentials into AgentHub;
- managing ChatGPT organizations, plans, or access-token issuance;
- exposing stored credential contents through the API;
- provider-specific model pickers or reasoning controls;
- replacing Kubernetes isolation with hook policies;
- supporting custom musl/Alpine session images.

## Acceptance criteria

The feature is complete when:

1. New and migrated sessions retain correct provider/auth semantics.
2. All three modes work with both agents.
3. Subscription and API-key billing are selectable per session for both agents.
4. Codex subscription login and refresh persist in the background without leaking tokens.
5. A pod receives only its selected credential.
6. Autonomous/Scheduled command and tool policies deny unmatched actions.
7. Claude behavior and legacy sessions remain functional.
8. Unit, component, runtime, build, Helm, and Docker Desktop Kubernetes acceptance checks pass.

