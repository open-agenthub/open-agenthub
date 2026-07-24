# Cursor Agent Support Design

**Date:** 2026-07-25  
**Status:** Approved design  
**Scope:** Open AgentHub backend, frontend, agent runtimes, Helm chart, local Docker Desktop Kubernetes workflow, and documentation

## Objective

Add Cursor as a first-class session agent alongside Claude and Codex. A user can select Claude, Codex, or Cursor for Interactive, Autonomous, and Scheduled sessions and independently choose subscription authentication or API-key billing for each Cursor session.

The implementation uses a separate Cursor runtime image. It shares only provider-neutral terminal transport and persistence code with Claude and Codex; CLI installation, authentication, state, resume, MCP setup, policy configuration, and command construction remain provider-specific.

## Product behavior

### Session choices

Every session has:

- `agent`: `Claude`, `Codex`, or `Cursor`;
- `authMode`: `Subscription` or `ApiKey` for new sessions;
- `mode`: the existing `Interactive`, `Autonomous`, or `Scheduled` value.

The New Session UI continues to default to `Claude` and `Subscription`. Agent and authentication selectors remain independent. Duplicate and resume retain both values. Editing either value affects the next start or resume because it can change the runtime image and mounted credentials.

Cursor does not use the Claude-only legacy `Auto` authentication mode.

Session list, search, detail, and history views display the selected agent. Search includes the agent and authentication labels without displaying secret status or values.

### Authentication behavior

The pod receives only the credential selected for that session:

| Agent | Auth mode | Pod credential |
| --- | --- | --- |
| Cursor | Subscription | writable copy of the user's Cursor CLI credential file (file-backed store) |
| Cursor | API key | `CURSOR_API_KEY` from the general user credential Secret |
| Claude / Codex | (unchanged) | existing behavior |

Subscription mode never injects the Cursor API key. API-key mode never mounts the subscription Secret.

The Credentials screen adds a write-only Cursor API-key field beside the Anthropic and OpenAI API-key fields. Credential status responses expose booleans only (`cursorApiKey`, `cursorSubscription`). Empty fields retain the stored value; the existing clear mechanism can remove it.

An Interactive subscription session may start without a stored login. The Cursor runtime uses `agent login` with headless-friendly settings (`NO_OPEN_BROWSER=1`, `AGENT_CLI_CREDENTIAL_STORE=file`). When the provider credential file appears or changes after a refresh, a background watcher uploads it through the per-session internal callback. The backend validates JSON, expected top-level shape, and a fixed size limit before replacing the provider-specific user Secret. Invalid uploads never replace a previously valid credential.

Autonomous and Scheduled sessions perform an authentication preflight. If their selected credential is unavailable, the session fails before starting the provider CLI and records a clear, non-secret diagnostic in scrollback and status.

### Credential trust boundary

Session provider credentials are not isolated from code or tools executed as the agent user. In Cursor API-key Autonomous and Scheduled sessions, descendants may inherit `CURSOR_API_KEY`; in subscription sessions, the file-backed credential is readable by the same agent user. Only trusted repositories and prompts may be run with provider credentials present.

Policy configuration is a guardrail, not secret isolation. Network and pod isolation must be used to limit credential exposure and blast radius.

## Runtime architecture

### Images

Build and publish a third agent image:

- `agent-runtime-cursor`, containing the Cursor Agent CLI (`agent` / `cursor-agent`) and Cursor driver.

The image shares the provider-neutral Node runtime for:

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
- install provider policy/config files;
- recognize a failed resume that should fall back once to a fresh session.

Shared code must not contain provider CLI flags or provider credential paths. Provider code must not implement WebSocket or persistence transport.

### CLI contract (direction)

| Mode | Command pattern |
| --- | --- |
| Interactive | `agent` (TUI); resume via `--resume <chatId>` and/or restored home state |
| Autonomous / Scheduled | `agent -p --force --trust ...` with output format suitable for scrollback |

Exact flags are owned by `agent-runtime/cursor/driver.js` and pinned against a documented CLI version in the Dockerfile.

### Custom images

When no custom image is selected, the backend uses the default image for the selected agent. When a custom image is selected, the init container uses the selected provider runtime image and copies its common runtime, provider driver, Node binary/modules, provider CLI, entrypoint, and hooks into the shared runtime volume. The session container then launches that copied entrypoint.

Custom-image requirements remain glibc, bash, git, and curl. Documentation describes that the injected CLI follows the selected agent.

### Backend and Helm configuration

Backend options and Helm values expose `CursorAgentImage` alongside Claude and Codex. Registry, tag, pull policy, pull Secret, runtime class, resource limits, and custom-image policy remain shared unless a future requirement needs provider-specific overrides.

Development scripts build five images: backend, frontend, Claude runtime, Codex runtime, and Cursor runtime. CI image workflows build and publish all three runtime images. Plain Kubernetes manifests stay consistent with the Helm chart.

## Persistence and resume

The session record stores `Agent` and `AuthMode`. Cursor uses the same provider-neutral `AgentSessionId` field as other agents for conversation identity when the CLI accepts an explicit chat id.

New provider state objects use a distinct S3 name:

- Cursor: `cursor-state.tgz`.

State archives exclude credential files because subscription credentials have their own user-scoped Secret and must not be restored from older session state. Authentication restore always occurs after state restore so a stale archive cannot shadow a newer user credential.

If Cursor cannot accept a caller-selected conversation ID, the driver uses the restored Cursor home/config and resumes the latest or specified thread. The driver may fall back once to a fresh thread when state is absent or invalid, then reports subsequent failures normally.

Scheduled runs keep existing scheduling semantics. Each CronJob pod uses the session's selected provider, authentication mode, policy, and MCP configuration. It does not silently switch billing sources when the selected credential is missing.

## Command and tool policy

### Policy model

Autonomous and Scheduled sessions store an agent policy. For Cursor, the existing structured fields map to Cursor CLI permission tokens:

| AgentHub field | Cursor mapping |
| --- | --- |
| `allowedTools` | `permissions.allow` tokens such as `Shell(...)`, `Read(...)`, `Write(...)`, `WebFetch(...)` |
| `allowedMcpTools` | `Mcp(server:tool)` / `Mcp(server:*)` entries merged into the same allow list |
| `allowedCommands` | unused for Cursor in the UI (left empty); shell access is expressed via `Shell(...)` tokens |

Empty policy sections remain default-deny for automation. The UI shows Cursor-specific placeholders and hides or disables the shell-command-prefix field when the agent is Cursor.

### Enforcement

Before starting the CLI, the Cursor runtime writes `~/.cursor/cli-config.json` (or the configured `CURSOR_CONFIG_DIR`) from the session policy:

- Autonomous / Scheduled: `approvalMode: allowlist`, allow entries from policy, `--trust`, `--sandbox disabled` (containers often lack a reliable sandbox), and `-p --force` so approved actions do not prompt. `--force` does not override explicit deny rules.
- Interactive: normal Cursor approval flow; optional allow/deny seeds without `--force`.

Hooks or config-based denials are additional guardrails, not the isolation boundary. Pod security context, RBAC, resource limits, namespace separation, and NetworkPolicies remain mandatory. Product documentation must not describe the allowlist as a complete container sandbox.

## MCP translation

AgentHub retains the existing `.mcp.json`-shaped JSON as its public session configuration format. The Cursor image translates supported server entries into `~/.cursor/mcp.json` before starting the agent.

Autonomous and Scheduled runs pass `--approve-mcps` when MCP is configured so headless sessions are not blocked on MCP approval prompts.

Unknown fields that can be safely ignored produce a diagnostic warning. Missing required fields or unsupported transports fail validation before the provider starts. Raw MCP config remains stored so editing and duplication are lossless.

## Notifications and collaboration

The common transport continues to clear `question_pending` when a user reconnects or sends input. Cursor uses available CLI lifecycle signals or equivalent runtime-owned hooks to report that an interactive turn stopped and is waiting, wired through the existing callback token and internal-only backend route.

Owner, Collaborator, and Viewer behavior does not change. A collaborator with terminal input can influence the running agent, so sharing documentation continues to treat a writable shared session as access to the selected session's capabilities.

## Secret handling

Use a separate per-user Kubernetes Secret for Cursor subscription credentials in addition to Claude, Codex, and general API/Git credentials. Secret names sanitize the owner identically to current behavior. Pods have no Kubernetes service-account token and cannot read other Secrets through the Kubernetes API.

Provider credential uploads:

1. authenticate with the session callback token;
2. resolve the owner from the stored session, never from request data;
3. enforce the session's provider and subscription authentication mode;
4. enforce content type, size, JSON validity, and provider-specific structure;
5. atomically create or replace only the matching owner Secret;
6. avoid logging credential bodies, token fragments, or parsed identity claims.

Exact Cursor credential filename and JSON shape are pinned from the file-backed CLI store and covered by validator tests. Production documentation recommends Kubernetes encryption at rest and restricts backend RBAC to the session namespace. Local development uses synthetic credentials for persistence tests.

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

- enum serialization including `Cursor`;
- duplicate/edit/resume retention of agent and auth mode;
- credential status, merge, clear, JSON validation, and Cursor provider Secret storage;
- exact pod/CronJob image, volume, mount, and environment selection for every Cursor auth combination;
- absence of unselected credentials;
- custom-image init runtime selection for Cursor;
- Cursor state S3 key `cursor-state.tgz`;
- structured policy passthrough for Cursor sessions.

### Runtime tests

Cover:

- exact fresh/resume CLI commands for every mode;
- subscription restore precedence and API-key scoping;
- watcher upload on create/change/refresh and no upload in API-key mode;
- Cursor MCP conversion fixtures;
- cli-config allow/deny generation from AgentHub policy;
- one-time resume fallback;
- Dockerfile containing the pinned Cursor CLI and provider files only.

No real subscription token or API key is used in automated tests.

### Frontend tests

Cover:

- agent options include Cursor;
- Cursor-aware permission fields and examples;
- API payloads for create, edit, and duplicate;
- credential status and Cursor API-key write/clear behavior;
- agent labels in list, detail, history, and search.

### Build and chart tests

Run backend tests, frontend tests/build, runtime tests, Cursor runtime Docker build, `helm lint`, and rendered-template assertions for the third image configuration. CI must build all three runtime images before the feature is considered complete.

## Docker Desktop Kubernetes acceptance

Use the existing guarded setup scripts, which refuse any kubectl context other than `docker-desktop`.

1. Build backend, frontend, Claude, Codex, and Cursor runtimes with the local tag.
2. Deploy the development Helm values with all runtime images and wait for rollout health.
3. Create Cursor Interactive and Autonomous sessions plus a Cursor Scheduled session.
4. Inspect generated Pod/CronJob specs to prove only the selected credential is present.
5. Exercise subscription persistence with synthetic auth fixtures and prove updated files reach only the correct owner/provider Secret.
6. Exercise API-key preflight, allowed and denied permission tokens, MCP conversion, pause/resume, terminal reconnect, and status transitions.
7. Start an unauthenticated Interactive Cursor session and verify that it reaches login.
8. Do not copy a developer workstation's Cursor credentials into the cluster. A real subscription login is completed only by the user in the session terminal.

The acceptance report records commands, relevant non-secret output, image digests/tags, test counts, and any environment limitation.

## Documentation and release compatibility

- README and deploy docs describe Cursor selection, both auth modes, the third runtime image, and the trusted-code credential boundary.
- Release notes call out the additional Cursor runtime image so private registries mirror it before enabling Cursor sessions.
- The API remains backward compatible for existing Claude and Codex session records.

## Out of scope

- Cursor SDK (`@cursor/sdk` / `cursor-sdk`) and Cloud Agents API
- ACP (`agent acp`) as the session transport instead of PTY
- A generic third-party agent plugin framework
- Desktop IDE run-mode files beyond the CLI (`permissions.json` of the IDE product)
- Automatic CLI self-update inside a running pod

## Project constraint

This is an independent open-source project (Open AgentHub by Maik Boltze / `open-agenthub`). Commits, documentation, code comments, configuration, and examples must not associate the project with any unrelated company brand.

## Acceptance criteria

1. Users can create Interactive, Autonomous, and Scheduled sessions with agent `Cursor`.
2. Users can choose Subscription or API-key auth independently per Cursor session.
3. Only the selected credential is present in the pod.
4. Subscription credentials persist per user across pods via the internal upload callback.
5. Autonomous/Scheduled fail closed when the selected credential is missing.
6. Cursor policy maps to CLI `cli-config.json` permission tokens; automation is default-deny.
7. MCP config from AgentHub is translated for the Cursor CLI.
8. Pause/resume restores Cursor state without restoring stale credentials over newer Secrets.
9. Helm, k8s manifests, setup-dev, and CI include the Cursor runtime image.
10. Focused automated tests and a Docker Desktop acceptance report cover the above without real secrets in CI.
