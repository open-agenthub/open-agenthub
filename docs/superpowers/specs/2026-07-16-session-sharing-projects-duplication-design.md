# Session Sharing, Projects, and Duplication Design

**Date:** 2026-07-16
**Status:** Approved design
**Scope:** Open AgentHub Community Edition, Enterprise Edition, agent runtime, frontend, Helm-based local development

## Objective

Add three related capabilities:

1. Community Edition users can organize sessions into personal projects.
2. Community Edition users can duplicate a session's reusable settings into a new independent session.
3. Enterprise Edition owners can share a live session with known users or secret links, granting read-only or collaborative access and enforcing a session-wide MCP restriction policy.

The implementation must preserve the existing open-core boundary: project organization and duplication remain in the AGPL-licensed core, while sharing and sharing-specific MCP controls live under `ee/` and require a valid enterprise license.

## Product Decisions

### Projects

- Projects are personal containers owned by one user.
- A session belongs to zero or one project.
- Sessions without a project appear under **Ungrouped**.
- Users can create, rename, recolor, reorder, and delete projects.
- Deleting a project does not delete sessions; affected sessions become ungrouped.
- Project names are unique per owner using case-insensitive comparison.

### Session duplication

- Only a session owner can duplicate that session.
- Duplication creates a new independent session with new AgentHub, callback, and Claude session identifiers.
- The owner chooses a new title, target project, and whether to retain MCP configuration.
- The duplicate copies mode, repositories, initial prompt, schedule, custom image, root mode, CPU, memory, and allowed tool rules.
- The duplicate does not copy conversation state, transcript, artifacts, runtime status, timestamps, callback tokens, shares, links, or sharing policies.
- Personal credentials are never copied because they remain in the existing per-user credential stores. The new session continues to use the owner's credentials at runtime through the established mechanism.
- MCP configuration may be copied only within the same owner's duplicate. A recipient never receives the raw configuration through sharing.
- Interactive duplicates start immediately, consistent with normal interactive session creation. Autonomous and scheduled duplicates use the copied prompt and schedule and follow the existing creation behavior for those modes.

### Sharing

- A session owner can share with a known instance user or create a secret link.
- Every grant has one of two roles:
  - `Viewer`: may view live terminal output and transcripts.
  - `Collaborator`: has viewer rights and may send input and resize events to the shared Claude terminal.
- The owner chooses either role for both direct grants and secret links.
- The owner may change a role or revoke a grant at any time.
- Secret links may have an optional expiration time.
- Secret link plaintext tokens are returned once at creation. Only a cryptographic token hash is stored.
- Known users see direct shares in their normal session list under a **Shared with me** grouping.
- Shared-session responses expose the owner identity and effective role but never raw MCP configuration or owner-only settings.

### Owner-only operations

Regardless of a share role, only the owner may:

- open the shell;
- edit session settings;
- assign the session to a project;
- duplicate the session;
- pause, resume, or delete the session;
- create, edit, or revoke shares and links;
- configure MCP sharing restrictions.

## Security Model

### Central authorization

A central `SessionAccessService` resolves an authenticated owner, a directly shared user, or a secret-link token into an effective access result. REST endpoints, transcripts, terminal WebSockets, and shell WebSockets must use this service rather than implementing separate role checks.

The result distinguishes:

- no access;
- viewer access;
- collaborator access;
- owner access.

Authorization is enforced server-side. Hiding unavailable frontend controls is usability, not a security boundary.

### Secret links

- Generate tokens with a cryptographically secure random number generator and at least 256 bits of entropy.
- Encode tokens using URL-safe Base64 without padding.
- Store a SHA-256 hash of the token, never the token itself.
- Compare fixed-size hashes using a constant-time comparison.
- Reject expired, revoked, malformed, or unknown tokens without disclosing which condition occurred.
- Do not place link tokens in logs.
- Update `last_used_at` after successful authorization without making authorization depend on that write succeeding.

### Shared terminal behavior

- Viewers receive terminal output but server-side WebSocket handling discards or rejects their input and resize frames.
- Collaborator input is forwarded to the same Claude terminal process in arrival order.
- Collaborators cannot access the separate shell WebSocket.
- Direct user access uses OIDC identity. Secret-link access uses dedicated token-bearing REST and WebSocket routes so the normal owner routes do not accept anonymous token parameters.

### MCP restrictions

Claude Code permission rules can deny an MCP server with `mcp__server` or all its tools with `mcp__server__*`, and can deny a specific tool with `mcp__server__tool`. Claude Code does not preserve the human author of a terminal message through a later MCP call. Per-collaborator MCP authorization inside one shared Claude process is therefore not a reliable security boundary.

The design uses a session-wide sharing policy:

- The owner can block complete MCP servers and specific MCP tools.
- Restrictions apply to every participant, including the owner, while configured.
- A dedicated MCP policy `PreToolUse` hook runs for interactive, autonomous, and scheduled sessions.
- The hook sends the session callback token and tool name to a fast internal policy endpoint.
- The backend immediately returns allow or deny based on the stored policy.
- A denied tool is blocked before execution.
- A backend timeout or malformed response fails closed for MCP calls when an MCP restriction policy exists; sessions without a policy continue through Claude Code's normal permission system.
- The existing interactive approval hook remains responsible for out-of-band approval and is not duplicated by the policy hook.
- A policy update takes effect on the next MCP call without restarting the pod.

The sharing dialog warns that existing transcripts may already contain information produced by MCP tools before a restriction was applied. When recipients need different MCP capabilities, the owner creates a duplicate with a filtered or removed MCP configuration and shares that independent session.

## Data Model

Schema changes follow the existing idempotent startup migration pattern.

### Community Edition

`projects`:

| Column | Type | Rules |
|---|---|---|
| `id` | text | primary key |
| `owner` | text | required, indexed |
| `name` | text | required |
| `color` | text | nullable, validated CSS hex color |
| `sort_order` | integer | required, default 0 |
| `created_at` | timestamptz | required |
| `updated_at` | timestamptz | required |

A unique index on `(owner, lower(name))` enforces owner-local names.

New `sessions` columns:

| Column | Type | Purpose |
|---|---|---|
| `project_id` | text | nullable project reference |
| `prompt` | text | nullable original prompt |
| `allowed_tools` | text | JSON array, nullable for backward compatibility |

Project deletion is performed transactionally by clearing `sessions.project_id` for the same owner and then deleting the project. Ownership is checked in every project query.

### Enterprise Edition

`session_shares`:

| Column | Type | Rules |
|---|---|---|
| `session_id` | text | required |
| `recipient_owner` | text | required |
| `role` | text | `Viewer` or `Collaborator` |
| `created_at` | timestamptz | required |
| `updated_at` | timestamptz | required |

The primary key is `(session_id, recipient_owner)`. Owners cannot share a session with themselves. Recipients must exist in `app_users`.

`session_share_links`:

| Column | Type | Rules |
|---|---|---|
| `id` | text | primary key, non-secret display identifier |
| `session_id` | text | required |
| `token_hash` | bytea | unique and required |
| `role` | text | `Viewer` or `Collaborator` |
| `expires_at` | timestamptz | nullable |
| `created_at` | timestamptz | required |
| `updated_at` | timestamptz | required |
| `last_used_at` | timestamptz | nullable |

`session_mcp_policies`:

| Column | Type | Rules |
|---|---|---|
| `session_id` | text | primary key |
| `blocked_servers` | text | JSON string array |
| `blocked_tools` | text | JSON string array of full Claude tool names |
| `updated_at` | timestamptz | required |

Session deletion removes associated sharing rows and MCP policy rows. The implementation performs explicit cleanup because the current sessions schema is migration-oriented and does not rely on foreign keys.

## API Design

### Community Edition project API

- `GET /api/projects`: list the current owner's projects in sort order.
- `POST /api/projects`: create a project from `name` and optional `color`.
- `PATCH /api/projects/{id}`: update name, color, or sort order.
- `DELETE /api/projects/{id}`: delete the project and ungroup its sessions.

### Community Edition session API

- Extend `PATCH /api/sessions/{id}` with `projectId`.
- `POST /api/sessions/{id}/duplicate` accepts:

```json
{
  "title": "Copy of session",
  "projectId": null,
  "includeMcp": true
}
```

It returns the newly created `SessionInfo`.

### Enterprise Edition owner API

- `GET /api/ee/sessions/{id}/shares`
- `POST /api/ee/sessions/{id}/shares/users`
- `PATCH /api/ee/sessions/{id}/shares/users/{recipient}`
- `DELETE /api/ee/sessions/{id}/shares/users/{recipient}`
- `POST /api/ee/sessions/{id}/shares/links`
- `PATCH /api/ee/sessions/{id}/shares/links/{linkId}`
- `DELETE /api/ee/sessions/{id}/shares/links/{linkId}`
- `PUT /api/ee/sessions/{id}/mcp-policy`

Creation of a share link returns its metadata plus the one-time URL. Listing links returns metadata only.

### Shared access API

- Authenticated direct shares are included in `GET /api/sessions` with `accessRole` and `sharedBy` fields.
- Existing owner URLs remain valid for directly shared users where the central access service authorizes the requested read or terminal operation.
- Secret links use `/api/shared/{token}/session` and `/api/shared/{token}/transcript`.
- Secret-link terminal access uses `/ws/shared/{token}/terminal`.
- No secret-link shell route exists.

### Error behavior

- `400 Bad Request`: invalid role, project, color, expiration, or MCP policy input.
- `402 Payment Required`: enterprise sharing requested without an enabled enterprise license, following the existing enterprise gate convention.
- `403 Forbidden`: authenticated principal exists but lacks the required role.
- `404 Not Found`: owner-scoped resource does not exist; secret links also use this response for invalid, expired, or revoked tokens.
- `409 Conflict`: duplicate project name or incompatible session state.

## Frontend Design

### Project grouping

- The sidebar renders collapsible project groups and an ungrouped section.
- Each header shows the project name, optional color, and visible session count.
- Search filters sessions across all projects and hides empty groups while active.
- Project controls support create, rename, recolor, reorder, and delete.
- Session edit controls include project assignment.

### Session actions

Owner sessions expose edit, duplicate, share, pause/resume, and delete as applicable. Shared sessions show an owner label and a `Viewer` or `Collaborator` badge. Owner-only actions are absent for recipients.

The duplicate dialog asks for:

- title;
- target project;
- whether to include MCP configuration.

### Sharing dialog

The EE sharing dialog contains:

1. **People**: choose a known user, assign a role, change it, or revoke access.
2. **Secret links**: choose a role and optional expiration, create, copy, edit, or revoke a link.
3. **MCP security**: list configured server names, allow blocking whole servers, accept exact full tool names for finer blocking, and explain the session-wide effect.

The one-time link is clearly distinguished from stored link metadata. The dialog never displays secret values embedded in MCP configuration.

## Local Kubernetes Development

Add repository-root scripts:

- `setup-dev.ps1`
- `setup-dev.sh`

Both scripts provide equivalent behavior:

1. Verify Docker, `kubectl`, and Helm are installed.
2. Require a local Docker Desktop Kubernetes context and refuse unrelated contexts.
3. Build `backend`, `frontend`, and `agent-runtime` images with a local development tag.
4. Generate a random PostgreSQL password without printing it.
5. Install or upgrade the Helm release idempotently into dedicated control-plane and session namespaces.
6. Configure local images, `IfNotPresent` pull policy, ephemeral PostgreSQL storage, one backend replica, disabled ingress, and development authentication.
7. Wait for PostgreSQL, backend, and frontend rollouts using condition-based waits.
8. Verify `/healthz` and the frontend service before reporting success.
9. Start a foreground port-forward to expose the frontend at `http://localhost:8080` and explain how to stop it.
10. Print commands for logs, redeployment, and uninstall.

### Development authentication

Production behavior remains unchanged. A development-only authentication handler can be enabled explicitly through Helm values used by the setup scripts. It reads a fixed test identity header so local integration tests can simulate an owner, viewer, and collaborator without an external identity provider.

Safety requirements:

- The handler is disabled by default.
- Startup rejects it unless the environment is `Development`.
- Production chart defaults do not enable or document it as a deployment option.
- The normal no-OIDC `dev` identity remains the default outside the dedicated integration-test flow.

## Testing Strategy

Implementation follows red-green-refactor cycles.

### Backend tests

- Project ownership, case-insensitive uniqueness, updates, ordering, and deletion behavior.
- Persistence of prompt, allowed tools, and project assignment.
- Duplication field inclusion and exclusion.
- Access resolution for owner, viewer, collaborator, missing grant, expired link, and revoked link.
- One-time token generation, hash-only persistence, and constant-time verification behavior.
- Owner-only endpoint enforcement.
- Sanitization of shared session responses.
- MCP server and exact-tool policy matching, including fail-closed behavior when restrictions exist.
- Enterprise license gate behavior.

### Frontend tests

- Project grouping and ungrouped sessions.
- Search behavior across groups.
- Shared owner and role labels.
- Owner-only action visibility.
- Viewer input disabled and collaborator input enabled.
- Duplicate and sharing dialog payloads.

### Agent runtime tests

- MCP policy hook allows an unrestricted MCP tool.
- MCP policy hook denies a blocked server.
- MCP policy hook denies an exact blocked tool.
- Restricted policy fails closed when the policy endpoint is unavailable.
- Sessions without a policy preserve normal Claude permission behavior.

### Docker Desktop end-to-end verification

After unit and build verification, test on the local cluster:

1. Create a project and assign a session.
2. Duplicate session settings and verify excluded state is absent.
3. Create viewer and collaborator direct grants.
4. Verify viewer input is rejected server-side.
5. Verify collaborator input reaches the shared terminal.
6. Create, use, expire or revoke a secret link.
7. Verify a blocked MCP tool is denied by the runtime hook.
8. Verify owner-only operations remain protected.

## Documentation and Compatibility

- Update the root README feature list and usage sections for CE projects, duplication, and EE sharing.
- Update `ee/README.md` to move session sharing from planned to implemented and document the security model.
- Document local Docker Desktop setup without making development authentication a production recommendation.
- Existing rows remain valid with nullable/defaulted columns.
- Existing owner routes and current ungrouped session behavior remain compatible.
- No new external runtime dependency is required beyond the existing PostgreSQL, Kubernetes, Vue, and .NET stack.

## Research Basis

- Claude Code MCP scopes and project approval behavior: <https://code.claude.com/docs/en/mcp>
- Claude Code MCP permission rule syntax and deny precedence: <https://code.claude.com/docs/en/permissions>
- Claude Code CLI tool restriction flags: <https://code.claude.com/docs/en/cli-usage>
- Codex project organization concepts: <https://openai.com/academy/working-with-codex/>

The key inference from these sources is that MCP deny rules can reliably block servers and tools for a Claude process, but cannot express a different policy for each human sharing that same terminal process. The session-wide policy plus independently duplicated restricted sessions is therefore the enforceable design.
