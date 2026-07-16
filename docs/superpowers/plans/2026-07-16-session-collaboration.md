# Session Collaboration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver CE project grouping and session duplication, EE live sharing with enforceable MCP restrictions, and a reproducible Docker Desktop Kubernetes development setup.

**Architecture:** Five workers own disjoint file sets: CE backend, EE backend, agent runtime, frontend, and deployment/documentation. A final integration task wires dependency injection, owner/shared authorization, routes, and full-system verification after those slices land.

**Tech Stack:** .NET 10 / ASP.NET Core, Npgsql/PostgreSQL, KubernetesClient, Vue 3/Vite, Node.js, Bash/PowerShell, Helm 3, Docker Desktop Kubernetes.

## Global Constraints

- Project organization and duplication remain in the AGPL-licensed core; sharing and sharing-specific MCP controls live under `ee/` and require a valid enterprise license.
- A shared session role is exactly `Viewer` or `Collaborator`; only owners may mutate lifecycle, settings, shell, projects, duplication, shares, or MCP policy.
- Secret-link tokens use at least 256 random bits, URL-safe Base64, SHA-256 hash-only storage, and constant-time hash comparison.
- Shared DTOs never return raw MCP configuration, tokens, or owner-only settings.
- MCP restrictions are session-wide, apply to the owner too, and are enforced before tool execution.
- Development header authentication is disabled by default and rejected outside the `Development` environment.
- Do not add new external runtime dependencies.
- Preserve existing nullable/defaulted-row compatibility and idempotent startup migrations.
- Do not include the prohibited company name defined in the repository instructions in code, comments, docs, tests, configuration, or commit messages.

---

### Task 1: CE projects and session duplication backend

**Files:**
- Create: `backend/Models/ProjectModels.cs`
- Create: `backend/Persistence/PostgresProjectStore.cs`
- Create: `backend/Controllers/ProjectsController.cs`
- Modify: `backend/Models/SessionModels.cs`
- Modify: `backend/Persistence/PostgresSessionStore.cs`
- Modify: `backend/Services/ISessionService.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`
- Modify: `backend/Controllers/SessionsController.cs`
- Create: `tests/AgentHub.Api.Tests/ProjectModelTests.cs`
- Create: `tests/AgentHub.Api.Tests/SessionDuplicationTests.cs`

**Interfaces:**
- Produces `ProjectInfo`, `CreateProjectRequest`, and `UpdateProjectRequest` in `AgentHub.Api.Models`.
- Produces `IProjectStore` with `InitializeAsync`, `ListAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync`, and `DeleteAsync`.
- Extends `SessionRecord` and `SessionInfo` with `string? ProjectId`, `string? Prompt`, and `IReadOnlyList<string> AllowedTools`.
- Produces `DuplicateSessionRequest(string Title, string? ProjectId, bool IncludeMcp)` and `ISessionService.DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct)`.
- Integration task registers `IProjectStore` and calls `InitializeAsync`.

- [ ] **Step 1: Write model validation tests**

```csharp
[Theory]
[InlineData("Project", true)]
[InlineData("", false)]
[InlineData("   ", false)]
public void ProjectNameValidation_IsDeterministic(string name, bool expected)
    => Assert.Equal(expected, ProjectValidation.IsValidName(name));

[Theory]
[InlineData(null, true)]
[InlineData("#12abEF", true)]
[InlineData("red", false)]
public void ProjectColorValidation_AcceptsOnlyHex(string? color, bool expected)
    => Assert.Equal(expected, ProjectValidation.IsValidColor(color));
```

- [ ] **Step 2: Run the focused tests and confirm RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~ProjectModelTests"`

Expected: compilation fails because `ProjectValidation` and project models do not exist.

- [ ] **Step 3: Implement project models and validation**

```csharp
namespace AgentHub.Api.Models;

public sealed record ProjectInfo(string Id, string Name, string? Color, int SortOrder);
public sealed record CreateProjectRequest(string Name, string? Color);
public sealed record UpdateProjectRequest(string? Name, string? Color, int? SortOrder);

public static class ProjectValidation
{
    public static bool IsValidName(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 80;

    public static bool IsValidColor(string? value)
        => value is null || System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$");
}
```

- [ ] **Step 4: Implement the owner-scoped project store and controller**

Use parameterized Npgsql commands. `DeleteAsync(owner, id)` must execute these statements in one transaction:

```sql
UPDATE sessions SET project_id = NULL, updated_at = now()
WHERE owner = @owner AND project_id = @id;
DELETE FROM projects WHERE owner = @owner AND id = @id;
```

Map duplicate-name PostgreSQL error `23505` to HTTP `409`; invalid names/colors to `400`; missing owner-scoped project to `404`.

- [ ] **Step 5: Write duplication tests before implementation**

```csharp
[Fact]
public void DuplicateRequest_CopiesReusableFieldsAndExcludesState()
{
    var source = SessionDuplication.CopyableRequest(new SessionRecord
    {
        Id = "old", Owner = "alice", Title = "Original", Mode = SessionMode.Autonomous,
        ClaudeSessionId = "old-claude", CallbackToken = "old-token", Status = "Succeeded",
        Prompt = "Run tests", AllowedToolsJson = "[\"Read\"]", ProjectId = "p1",
        McpConfigJson = "{\"mcpServers\":{}}"
    }, new DuplicateSessionRequest("Copy", "p2", false));

    Assert.Equal("Copy", source.Title);
    Assert.Equal("p2", source.ProjectId);
    Assert.Equal("Run tests", source.Prompt);
    Assert.Equal(["Read"], source.AllowedTools);
    Assert.Null(source.McpConfigJson);
}
```

- [ ] **Step 6: Run the duplication test and confirm RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~SessionDuplicationTests"`

Expected: compilation fails because duplicate models and fields do not exist.

- [ ] **Step 7: Persist copyable settings and implement duplication**

Add idempotent columns:

```sql
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS project_id TEXT;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS prompt TEXT;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS allowed_tools TEXT;
CREATE INDEX IF NOT EXISTS idx_sessions_project ON sessions(owner, project_id);
```

`DuplicateSessionAsync` loads by owner, validates the target project through `IProjectStore`, constructs a normal `CreateSessionRequest`, and calls `CreateSessionAsync`. Never reuse IDs, status, transcript, callback token, artifact key, or share state.

- [ ] **Step 8: Run CE backend tests and commit**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~ProjectModelTests|FullyQualifiedName~SessionDuplicationTests"`

Expected: all focused tests pass.

Commit: `feat: add projects and session duplication`

---

### Task 2: EE sharing persistence, authorization, and API

**Files:**
- Create: `ee/backend/Sharing/ShareModels.cs`
- Create: `ee/backend/Sharing/SessionShareStore.cs`
- Create: `ee/backend/Sharing/SessionAccessService.cs`
- Create: `ee/backend/Sharing/SharingController.cs`
- Create: `ee/backend/Sharing/SharedSessionsController.cs`
- Create: `ee/backend/Sharing/McpPolicyMatcher.cs`
- Create: `tests/AgentHub.Api.Tests/SessionAccessTests.cs`
- Create: `tests/AgentHub.Api.Tests/ShareTokenTests.cs`
- Create: `tests/AgentHub.Api.Tests/McpPolicyTests.cs`

**Interfaces:**
- Produces enum `ShareRole { Viewer, Collaborator }` and enum `SessionAccessLevel { None, Viewer, Collaborator, Owner }`.
- Produces `SessionAccessResult(SessionRecord Session, SessionAccessLevel Level, string? SharedBy)`.
- Produces `ISessionAccessService.ResolveUserAsync(string principal, string sessionId, CancellationToken ct)` and `ResolveTokenAsync(string token, CancellationToken ct)`.
- Produces `SessionShareStore.InitializeAsync`, `ListForOwnerAsync`, `ListSharedWithAsync`, direct grant CRUD, link CRUD/resolution, MCP policy CRUD, and `DeleteForSessionAsync`.
- Produces `McpPolicyMatcher.IsBlocked(string toolName, IReadOnlyCollection<string> blockedServers, IReadOnlyCollection<string> blockedTools)`.
- Integration task registers services, initializes the store, supplies sanitized `SessionInfo`, and wires normal controllers/WebSockets.

- [ ] **Step 1: Write access precedence tests**

```csharp
[Theory]
[InlineData(true, null, SessionAccessLevel.Owner)]
[InlineData(false, ShareRole.Collaborator, SessionAccessLevel.Collaborator)]
[InlineData(false, ShareRole.Viewer, SessionAccessLevel.Viewer)]
[InlineData(false, null, SessionAccessLevel.None)]
public void EffectiveAccess_OwnerWins(bool owns, ShareRole? role, SessionAccessLevel expected)
    => Assert.Equal(expected, SessionAccessRules.Resolve(owns, role));
```

- [ ] **Step 2: Run access tests and confirm RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~SessionAccessTests"`

Expected: compilation fails because sharing types do not exist.

- [ ] **Step 3: Implement sharing models and pure authorization rules**

```csharp
public static class SessionAccessRules
{
    public static SessionAccessLevel Resolve(bool owns, ShareRole? role) => owns
        ? SessionAccessLevel.Owner
        : role switch
        {
            ShareRole.Collaborator => SessionAccessLevel.Collaborator,
            ShareRole.Viewer => SessionAccessLevel.Viewer,
            _ => SessionAccessLevel.None
        };
}
```

- [ ] **Step 4: Write token and MCP matcher tests**

```csharp
[Fact]
public void GeneratedToken_RoundTripsThroughHashWithoutStoringPlaintext()
{
    var issued = ShareTokens.Issue();
    Assert.True(issued.Token.Length >= 43);
    Assert.Equal(32, issued.Hash.Length);
    Assert.True(ShareTokens.Matches(issued.Token, issued.Hash));
    Assert.False(ShareTokens.Matches(issued.Token + "x", issued.Hash));
}

[Theory]
[InlineData("mcp__slack__search", true)]
[InlineData("mcp__github__search", false)]
public void BlockedServer_MatchesClaudeToolName(string tool, bool expected)
    => Assert.Equal(expected, McpPolicyMatcher.IsBlocked(tool, ["slack"], []));
```

- [ ] **Step 5: Run token/policy tests and confirm RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~ShareTokenTests|FullyQualifiedName~McpPolicyTests"`

Expected: compilation fails because token and policy helpers do not exist.

- [ ] **Step 6: Implement tokens and exact MCP matching**

Use `RandomNumberGenerator.GetBytes(32)`, URL-safe unpadded Base64, `SHA256.HashData`, and `CryptographicOperations.FixedTimeEquals`. Server matching accepts exactly `mcp__{server}` or a prefix `mcp__{server}__`; tool matching is ordinal equality against the full tool name.

- [ ] **Step 7: Implement idempotent sharing persistence**

Create `session_shares`, `session_share_links`, and `session_mcp_policies` exactly as specified. All owner mutations first verify `sessions.owner`. Direct recipients must exist in `app_users`; self-sharing returns validation failure. Link resolution rejects expired links and updates `last_used_at` after successful lookup.

- [ ] **Step 8: Implement EE controllers and license gate**

All owner routes require `IEnterpriseLicense.Enabled`. Use `402` when disabled. `SharedSessionsController` returns a sanitized DTO with `McpConfigJson = null`, no image/resources/repos configuration fields beyond display-safe repository URLs, and the effective role.

- [ ] **Step 9: Run EE tests and commit**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~SessionAccessTests|FullyQualifiedName~ShareTokenTests|FullyQualifiedName~McpPolicyTests"`

Expected: all focused tests pass.

Commit: `feat: add enterprise session sharing`

---

### Task 3: Agent-runtime MCP policy enforcement

**Files:**
- Create: `agent-runtime/session-agent/mcp-policy-hook.sh`
- Modify: `agent-runtime/entrypoint.sh`
- Modify: `agent-runtime/session-agent/package.json`
- Create: `agent-runtime/session-agent/test/mcp-policy-hook.test.js`

**Interfaces:**
- Calls `POST $AGENTHUB_CALLBACK_URL/mcp-policy` with `{ "tool": "mcp__server__tool" }` and `X-Agent-Token`.
- Expects `{ "restricted": boolean, "decision": "allow" | "deny" }`.
- Produces a Claude `PreToolUse` hook decision only for MCP tool names.
- EE backend task implements the internal endpoint; integration task wires the controller dependency.

- [ ] **Step 1: Write hook behavior tests**

Use a temporary local HTTP server and spawn the script with hook JSON on stdin. Assert:

```javascript
assert.equal(result.decision, 'deny')       // blocked response
assert.equal(result.decision, undefined)    // unrestricted response preserves normal flow
assert.equal(result.decision, 'deny')       // endpoint failure with AGENTHUB_MCP_POLICY=1
assert.equal(result.decision, undefined)    // endpoint failure without a configured policy
```

- [ ] **Step 2: Run tests and confirm RED**

Run: `npm test -- --test-name-pattern="MCP policy"`

Expected: failure because `mcp-policy-hook.sh` does not exist.

- [ ] **Step 3: Implement the hook**

The script reads stdin once, extracts `tool_name`, exits with `{}` for non-MCP tools, calls the endpoint with a short curl timeout, and prints:

```json
{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Blocked by the session MCP sharing policy"}}
```

for denied calls. On endpoint failure it prints the same denial only when `AGENTHUB_MCP_POLICY=1`; otherwise it prints `{}`.

- [ ] **Step 4: Register the policy hook for every mode**

Write `PreToolUse` hook entries so the policy hook runs before the existing interactive approval hook. Autonomous and scheduled sessions receive only the policy hook. Preserve notification hooks.

- [ ] **Step 5: Run runtime tests and commit**

Run: `npm test`

Expected: all runtime tests pass.

Commit: `feat: enforce session MCP sharing policy`

---

### Task 4: Frontend project, duplication, and sharing UI

**Files:**
- Create: `frontend/src/lib/projects.js`
- Create: `frontend/src/lib/projects.test.js`
- Create: `frontend/src/lib/access.js`
- Create: `frontend/src/lib/access.test.js`
- Create: `frontend/src/components/ProjectSidebar.vue`
- Create: `frontend/src/components/DuplicateSessionDialog.vue`
- Create: `frontend/src/components/ShareSessionDialog.vue`
- Modify: `frontend/src/api.js`
- Modify: `frontend/src/App.vue`
- Modify: `frontend/src/components/SessionList.vue`
- Modify: `frontend/src/components/EditSessionDialog.vue`
- Modify: `frontend/src/components/TerminalView.vue`
- Modify: `frontend/src/components/TerminalPane.vue`
- Modify: `frontend/src/style.css`

**Interfaces:**
- Consumes project and sharing routes exactly as defined in Tasks 1 and 2.
- Consumes session fields `projectId`, `accessRole`, and `sharedBy`.
- Produces `groupSessions(projects, sessions, query)` and `sessionCapabilities(session)` as pure tested helpers.
- `TerminalPane` receives `readonly: Boolean`; read-only mode does not register input forwarding.

- [ ] **Step 1: Write grouping and capability tests**

```javascript
it('groups matching sessions and preserves ungrouped', () => {
  const groups = groupSessions([{ id: 'p1', name: 'Core', sortOrder: 0 }], [
    { id: 's1', title: 'API', projectId: 'p1' },
    { id: 's2', title: 'Notes', projectId: null }
  ], '')
  expect(groups.map(g => [g.name, g.sessions.map(s => s.id)])).toEqual([
    ['Core', ['s1']], ['Ungrouped', ['s2']]
  ])
})

it('viewer cannot write or manage', () => {
  expect(sessionCapabilities({ accessRole: 'Viewer' })).toEqual({
    canWrite: false, canManage: false, canShell: false
  })
})
```

- [ ] **Step 2: Run frontend tests and confirm RED**

Run: `npm test -- --run`

Expected: module-not-found failures for the new helpers.

- [ ] **Step 3: Implement pure helpers**

`groupSessions` sorts projects by `sortOrder`, filters session title/repository case-insensitively, appends `Shared with me`, and appends `Ungrouped`. `sessionCapabilities` treats missing `accessRole` or `Owner` as owner, `Collaborator` as write-only collaboration, and `Viewer` as read-only.

- [ ] **Step 4: Add API methods**

Add project CRUD, `duplicateSession`, share user/link CRUD, MCP policy update, and shared-link fetch methods. Continue using the existing `req` error path and URL-encode IDs/recipients.

- [ ] **Step 5: Build the grouped sidebar and dialogs**

Use existing CSS variables and mobile breakpoint. The sharing dialog lists people, links, and MCP security without rendering raw MCP JSON. The duplicate dialog submits `{ title, projectId, includeMcp }`. Owner-only actions render only when `sessionCapabilities(session).canManage`.

- [ ] **Step 6: Enforce viewer read-only behavior in terminal components**

When `readonly` is true, terminal output remains connected, but `onData` and resize messages are not sent. Render a visible `Read-only shared session` notice. Collaborator sessions use the terminal route and cannot show the shell tab.

- [ ] **Step 7: Run frontend tests/build and commit**

Run: `npm test -- --run`

Run: `npm run build`

Expected: tests and production build pass.

Commit: `feat: add project and sharing interface`

---

### Task 5: Docker Desktop setup and documentation

**Files:**
- Create: `setup-dev.ps1`
- Create: `setup-dev.sh`
- Create: `helm/open-agenthub/values-dev.yaml`
- Modify: `helm/open-agenthub/templates/ingress.yaml`
- Modify: `helm/open-agenthub/templates/configmap.yaml`
- Modify: `helm/open-agenthub/values.yaml`
- Modify: `README.md`
- Modify: `ee/README.md`

**Interfaces:**
- Scripts accept optional `-NoPortForward` / `--no-port-forward` and default to foreground port-forward on port 8080.
- Scripts require context `docker-desktop`, release `agenthub-dev`, control namespace `agenthub-dev`, and sessions namespace `agenthub-dev-sessions`.
- `values-dev.yaml` sets local registry `open-agenthub-dev`, tag `local`, one backend replica, no ingress, ephemeral PostgreSQL, and development environment.
- Integration task adds the development-auth configuration keys consumed by the backend.

- [ ] **Step 1: Implement preflight and context refusal in both scripts**

PowerShell checks commands with `Get-Command`; Bash uses `command -v`. Both read `kubectl config current-context` and stop unless it equals `docker-desktop`. Neither script automatically switches away from an unrelated context.

- [ ] **Step 2: Implement local image builds and Helm deployment**

Build:

```text
open-agenthub-dev/backend:local
open-agenthub-dev/frontend:local
open-agenthub-dev/agent-runtime:local
```

Generate a 32-byte random PostgreSQL password, pass it only through Helm `--set-string`, and run `helm upgrade --install` with `values-dev.yaml`.

- [ ] **Step 3: Implement condition-based rollout and health verification**

Wait for the PostgreSQL StatefulSet and backend/frontend Deployments. Port-forward backend temporarily for `/healthz`, terminate that helper, then start the foreground frontend port-forward on 8080. Trap/`finally` cleanup must terminate helper processes.

- [ ] **Step 4: Gate ingress and development values**

Add `ingress.enabled` defaulting to `true`; wrap the ingress template. Add backend environment keys for `ASPNETCORE_ENVIRONMENT` and development test authentication, disabled in default values and enabled only in `values-dev.yaml`.

- [ ] **Step 5: Update documentation**

Document CE projects/duplication, EE sharing roles and session-wide MCP restrictions, and exact setup script usage. State that development test authentication is local-only and not a production deployment option.

- [ ] **Step 6: Validate scripts/templates and commit**

Run: `helm lint helm/open-agenthub -f helm/open-agenthub/values-dev.yaml --set-string postgres.password=test-only`

Run: `bash -n setup-dev.sh`

Run: `pwsh -NoProfile -Command "[scriptblock]::Create((Get-Content -Raw ./setup-dev.ps1)) | Out-Null"`

Expected: all validations exit 0.

Commit: `dev: add Docker Desktop Kubernetes setup`

---

### Task 6: Cross-cutting integration, security tests, and full verification

**Files:**
- Modify: `backend/Program.cs`
- Modify: `backend/Controllers/SessionsController.cs` only for access-service integration after reconciling Task 1
- Modify: `backend/Controllers/InternalController.cs`
- Modify: `backend/WebSockets/TerminalProxy.cs`
- Modify: `backend/Services/ISessionService.cs` only to reconcile merged contracts
- Modify: `backend/Services/KubernetesSessionService.cs` only to reconcile policy env and merged contracts
- Create: `backend/Auth/DevelopmentHeaderAuthHandler.cs`
- Create: `tests/AgentHub.Api.Tests/SharedSessionSecurityTests.cs`

**Interfaces:**
- Consumes all interfaces produced by Tasks 1–5.
- Provides one authorization path for REST and WebSockets.
- Adds `POST /internal/sessions/{id}/mcp-policy` returning `{ restricted, decision }`.

- [ ] **Step 1: Merge worker commits and resolve only contract-level conflicts**

Preserve each worker's tests. Do not weaken owner checks or expose raw configuration to make DTO mapping easier.

- [ ] **Step 2: Write cross-cutting security tests before wiring**

```csharp
[Fact]
public void SharedDto_RemovesOwnerOnlyConfiguration()
{
    var dto = SharedSessionSanitizer.Sanitize(SessionFixtures.Configured(), SessionAccessLevel.Viewer);
    Assert.Null(dto.McpConfigJson);
    Assert.False(dto.CanManage);
    Assert.Equal("Viewer", dto.AccessRole);
}

[Theory]
[InlineData(SessionAccessLevel.Viewer, false)]
[InlineData(SessionAccessLevel.Collaborator, true)]
[InlineData(SessionAccessLevel.Owner, true)]
public void TerminalInput_DependsOnRole(SessionAccessLevel level, bool expected)
    => Assert.Equal(expected, SessionAccessRules.CanWriteTerminal(level));
```

- [ ] **Step 3: Run security tests and confirm RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~SharedSessionSecurityTests"`

Expected: failure until sanitizer and integrated role helpers exist.

- [ ] **Step 4: Wire persistence, licensing, controllers, and development auth**

Register CE and EE stores/services, initialize schemas, keep development auth disabled by default, and throw at startup if configured outside `Development`. Preserve normal OIDC and current `dev` auth behavior.

- [ ] **Step 5: Wire REST and WebSocket access**

Owner mutation routes continue using owner-scoped service methods. Read/transcript routes resolve effective access. Terminal proxy receives `canWrite`; shell requires owner. Secret-link routes resolve tokens through the EE access service.

- [ ] **Step 6: Wire live MCP policy enforcement**

The internal endpoint authenticates the callback token, loads policy, returns unrestricted allow when no policy exists, and returns deterministic deny for a matched server/tool. Set `AGENTHUB_MCP_POLICY=1` in pods only when a policy row exists; the endpoint remains the source of current policy so updates are live.

- [ ] **Step 7: Run all unit tests and production builds**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`

Run: `dotnet build backend/AgentHub.Api.csproj -c Release`

Run: `npm test -- --run` in `frontend`

Run: `npm run build` in `frontend`

Run: `npm test` in `agent-runtime/session-agent`

Expected: every command exits 0.

- [ ] **Step 8: Scan repository-sensitive content and diff quality**

Run in PowerShell: `$policyLine = (Get-Content ..\AGENTS.md | Select-String 'darf.*nirgends' | Select-Object -First 1).Line; $policy = [regex]::Match($policyLine, '"([^\"]+)"').Groups[1].Value; rg -n -i --hidden --glob '!.git/**' --glob '!node_modules/**' --fixed-strings $policy .`

Expected: zero matches. The policy term is read dynamically from the repository instructions and is not copied into project artifacts or commits.

Run: `git diff --check`

Expected: exit 0.

- [ ] **Step 9: Deploy and test on Docker Desktop Kubernetes**

Run `./setup-dev.ps1 -NoPortForward` on Windows or `./setup-dev.sh --no-port-forward` on Unix. Verify projects, duplication, direct roles, link revocation/expiration, viewer write rejection, collaborator input, MCP denial, and owner-only actions through API tests against the local cluster. Start the foreground port-forward only after checks pass.

- [ ] **Step 10: Commit integration**

Commit: `feat: integrate collaborative sessions`
