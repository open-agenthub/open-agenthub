# OpenClaw Agent Support Design

**Date:** 2026-07-27  
**Status:** Approved design  
**Scope:** Open AgentHub Community Edition (OpenClaw sessions) and Enterprise Edition (admin-allowed agent kinds); backend, frontend, agent runtime, Helm, CI, and docs

## Objective

Add OpenClaw as a first-class CE session agent alongside Claude, Codex, and Cursor, using the same runtime-image + provider-driver pattern as Cursor.

Add an EE feature so admins can choose which agent kinds users may start on the instance (`Claude`, `Codex`, `Cursor`, `OpenClaw`). CE and unlicensed instances allow all agents.

## Product behavior (CE)

### Session choices

Every session has:

- `agent`: `Claude`, `Codex`, `Cursor`, or `OpenClaw`;
- `authMode`: `Subscription` or `ApiKey` for new sessions;
- `mode`: existing `Interactive`, `Autonomous`, or `Scheduled`;
- for OpenClaw + ApiKey only: `openClawApiKeySource`: `Anthropic` | `OpenAI` | `Cursor`.

Defaults remain `Claude` + `Subscription`. Duplicate and resume retain agent, auth mode, and (when applicable) API-key source. Editing agent or auth affects the next start/resume because image and mounted credentials can change.

OpenClaw does not use the Claude-only legacy `Auto` authentication mode.

Session list, search, detail, and history show the selected agent. Search includes agent and auth labels without secret values.

### Authentication behavior

| Agent | Auth mode | Pod credential |
| --- | --- | --- |
| OpenClaw | Subscription | writable copy of the user's OpenClaw credential file (`openclaw-{owner}` Secret) |
| OpenClaw | API key + Anthropic source | existing `anthropic_api_key` only |
| OpenClaw | API key + OpenAI source | existing `openai_api_key` only |
| OpenClaw | API key + Cursor source | existing `cursor_api_key` only |
| Claude / Codex / Cursor | (unchanged) | existing behavior |

Subscription mode never injects an API key. API-key mode never mounts the OpenClaw subscription Secret. The Credentials screen does **not** add new API-key fields for OpenClaw; it reuses Anthropic / OpenAI / Cursor keys. Credential status exposes `openclawSubscription` (boolean) in addition to existing flags.

Interactive subscription sessions may start without a stored login. The OpenClaw runtime runs a headless-friendly login/onboarding path. When the provider credential file appears or changes, a watcher uploads it through the per-session internal callback. The backend validates shape and size before replacing the OpenClaw user Secret. Invalid uploads never replace a previously valid credential.

Autonomous and Scheduled sessions preflight authentication. Missing selected credentials fail before starting the CLI, with a clear non-secret diagnostic in scrollback and status.

### Credential trust boundary

Same as Cursor: provider credentials in the pod are readable by the agent user and descendants. Policy is a guardrail, not secret isolation. Only trusted repos and prompts should run with credentials present.

## Runtime architecture

### Image

Build and publish `agent-runtime-openclaw` containing the OpenClaw CLI and OpenClaw driver. Shared Node transport remains provider-neutral (PTY, WebSocket, replay, shell terminal, callbacks, scrollback, state persistence, signals).

Provider driver owns:

- Interactive command (OpenClaw TUI / `openclaw` in the PTY);
- Autonomous / Scheduled command (`openclaw agent --local` with message/prompt);
- Resume command and missing-resume detection;
- State directory name and auth filename;
- Auth prepare + subscription file watcher;
- MCP / policy configuration where OpenClaw supports it.

### Persistence

- Session record stores `Agent`, `AuthMode`, and for OpenClaw ApiKey sessions `OpenClawApiKeySource`.
- State object: `openclaw-state.tgz` (excludes credential files).
- Auth restore always runs after state restore so stale archives cannot shadow newer credentials.
- Resume uses restored OpenClaw home/config and/or an explicit session id when the CLI accepts one; one fallback to a fresh session when state is absent/invalid, then normal failure reporting.

### Backend and Helm

Expose `OpenClawAgentImage` alongside Claude, Codex, and Cursor. Shared registry/tag/pull policy/resources unless a future need requires overrides. Dev scripts and CI build/publish the fourth runtime image. Custom-image init copies the selected provider runtime as today.

## Enterprise: allowed agents

### Semantics

| Context | Behavior |
| --- | --- |
| CE / no valid EE license | All agents allowed; no allowlist UI or enforcement |
| EE licensed, no stored config | All agents allowed (opt-in restriction) |
| EE licensed, stored whitelist | Only listed agents may be created, duplicated onto, updated to, started, or resumed |

### Enforcement

- Backend checks on create, duplicate, update (when agent changes), start, and resume.
- Violations return `403` with a clear error; never silently rewrite the agent.
- Frontend New/Edit/Duplicate selectors only list allowed agents.
- Admin Settings UI (EE) edits the instance whitelist (checkboxes per agent).
- Existing sessions whose agent is later disallowed remain visible; restart/resume is blocked until the agent is allowed again or the session is reconfigured to an allowed agent.

### Technical shape

- Core CE interface, e.g. `IAllowedAgentsProvider`, default implementation returns all known `AgentKind` values.
- EE store (Postgres) + admin controller under `ee/`, gated by `IEnterpriseLicense`, registered like usage limits / group roles.
- Public read endpoint (or session bootstrap payload) so the UI can filter selectors without requiring admin role.

## Error handling

- Missing ApiKey for the chosen source → preflight fail before CLI start.
- Missing subscription for Autonomous/Scheduled → preflight fail; Interactive may start and log in.
- Disallowed agent (EE) → `403`.
- Invalid credential upload → do not overwrite existing Secret.
- Unsupported `AgentKind` / invalid `openClawApiKeySource` → `400` on create/update.

## Testing

- Model validation: `OpenClaw` + Subscription/ApiKey; reject `Auto`; require `openClawApiKeySource` when ApiKey.
- Pod spec: correct image; ApiKey mounts only the selected existing key; Subscription mounts only OpenClaw secret.
- EE allowlist: CE = all; licensed default = all; restricted create/start/resume; admin CRUD.
- Runtime driver unit tests: command building, prepare/auth, resume fallback.
- Docs and commits must not mention any unrelated company names.

## Non-goals (this design)

- Gateway-first Control UI embedding as the primary Interactive UX (PTY/TUI first).
- New dedicated OpenClaw Anthropic/OpenAI API-key fields separate from existing credentials.
- Per-user or per-group agent allowlists (instance-wide only).
- Changing Claude/Codex/Cursor runtime behavior beyond allowlist enforcement.
