# MCP Catalog + OpenAPI/GraphQL Gateway Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship a personal + org MCP catalog with EE user/group ACL, session picker, and an AgentHub HTTP MCP gateway that wraps OpenAPI/GraphQL URLs for agents.

**Architecture:** Port MCP-only pieces from `feature/skill-library-mcp`, extend with `kind`/`secret_json`/`__org__`, assemble session `.mcp.json` from catalog ids + inline JSON + ephemeral API sources, and serve `api`/ephemeral wrappers via streamable-HTTP MCP on the API.

**Tech Stack:** .NET 8 (AgentHub.Api), Postgres, Vue 3 frontend, vitest, xUnit, Node `@modelcontextprotocol/sdk` for the gateway process **or** in-process MCP HTTP endpoint in the API (prefer in-process C# or a small Node sidecar under `backend/` / `mcp/api-gateway/` — decide in Task 8; default: Node service colocated and reverse-proxied, matching browser MCP SDK familiarity).

**Design:** `docs/superpowers/specs/2026-08-01-mcp-catalog-api-gateway-design.md`

**Worktree:** `.worktrees/mcp-catalog-api-gateway` on `feat/mcp-catalog-api-gateway`

**WIP source (read-only reference):** `.worktrees/skill-library-mcp` / branch `feature/skill-library-mcp`

---

### Task 1: MCP server record + validation (core models)

**Files:**
- Create: `backend/Library/LibraryModels.cs`
- Create: `backend/Library/LibraryValidation.cs`
- Create: `tests/AgentHub.Api.Tests/LibraryValidationTests.cs`

**Step 1: Write the failing test**

```csharp
public class LibraryValidationTests
{
    [Fact]
    public void ValidateMcpServerName_rejects_empty()
    {
        Assert.Throws<ArgumentException>(() => LibraryValidation.ValidateMcpServerName(""));
    }

    [Fact]
    public void ValidateRawConfig_requires_object()
    {
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig("{", kind: "raw"));
    }

    [Fact]
    public void ValidateApiConfig_requires_specUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            LibraryValidation.ValidateMcpServerConfig(
                """{"specType":"openapi"}""", kind: "api"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter LibraryValidationTests`

Expected: FAIL (types missing)

**Step 3: Write minimal implementation**

Port/adapt from WIP `LibraryModels.cs` / validation helpers. Include:

- `McpServerRecord` with `Kind`, `ConfigJson`, `SecretJson` (plaintext in memory after decrypt; store encrypts)
- `OrgOwner = "__org__"`
- `SaveMcpServerRequest`, `McpServerInfo` (ConfigJson only when `Mine`)
- `LibraryValidation.ValidateMcpServerName`, `ValidateKind`, `ValidateMcpServerConfig`

**Step 4: Run tests — Expected: PASS**

**Step 5: Commit**

```bash
git add backend/Library tests/AgentHub.Api.Tests/LibraryValidationTests.cs
git commit -m "feat(mcp-catalog): add MCP library models and validation"
```

---

### Task 2: McpServerStore (Postgres)

**Files:**
- Create: `backend/Library/McpServerStore.cs`
- Create: `tests/AgentHub.Api.Tests/LibraryStoresPostgresTests.cs` (or in-memory first + postgres if suite has pattern)
- Modify: `backend/Program.cs` — register `IMcpServerStore`

**Step 1: Write failing tests** for Create/ListByOwner/GetMany/Update/Delete with in-memory double first (port `InMemoryMcpServerStore` from WIP `LibraryTestSupport.cs`).

**Step 2: Implement** `IMcpServerStore` + Postgres table `mcp_servers` (`InitializeAsync` like other stores). Support `owner`, `kind`, `config_json`, `secret_json` (nullable text; encryption in Task 7).

**Step 3: Register in DI; run tests — PASS; commit**

```bash
git commit -m "feat(mcp-catalog): persist MCP servers in Postgres"
```

---

### Task 3: Library access (own + org without EE)

**Files:**
- Create: `backend/Library/LibraryAccessService.cs`
- Create: `tests/AgentHub.Api.Tests/LibraryAccessServiceTests.cs`
- Stub: `ILibraryShareReader` in core that returns empty when EE disabled

**Behavior:**
- Always list/resolve own ids
- Always include `__org__` entries when no license **or** when EE share allows (EE wired in Task 4)
- `ResolveMcpServersAsync(owner, ids, strict)` — strict throws on unknown/forbidden

**Step 1: Failing tests** (port from WIP, drop skills cases; add org-visible-without-license cases)

**Step 2: Implement MCP-only `ILibraryAccess`**

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): resolve personal and org MCP access"
```

---

### Task 4: EE library shares for MCP

**Files:**
- Create: `ee/backend/Library/LibraryShareModels.cs`
- Create: `ee/backend/Library/LibraryShareStore.cs`
- Create: `ee/backend/Library/LibraryController.cs` (shares endpoints only; no skills settings unless already required)
- Modify: `LibraryAccessService` to consult `ILibraryShareReader` when `_license.Enabled`
- Tests: extend `LibraryAccessServiceTests` for all/users/groups

**Step 1: Failing tests** for share matrix (port WIP)

**Step 2: Implement store + `GET/PUT /api/ee/library/mcp-servers/{id}/shares`** (402 without license)

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): EE user/group shares for MCP entries"
```

---

### Task 5: McpConfigAssembler + session `mcpServerIds`

**Files:**
- Create: `backend/Library/McpConfigAssembler.cs`
- Create: `tests/AgentHub.Api.Tests/McpConfigAssemblerTests.cs`
- Modify: `backend/Models/SessionModels.cs` — `McpServerIds` on create/update/info
- Modify: `backend/Persistence/PostgresSessionStore.cs` — column `mcp_server_ids`
- Modify: `backend/Services/KubernetesSessionService.cs` — resolve + assemble before secret create
- Modify: `backend/Services/SessionUpdateValidator.cs` as needed

**Assembler rules (from design):**
- Start from inline `mcpConfigJson`
- Add each resolved catalog entry by `Name`; skip if name already present (inline wins)
- For `kind=api`, emit HTTP MCP pointing at gateway URL (placeholder URL OK until Task 8; use `https://mcp.invalid/{id}` in unit tests)

**Step 1: Assembler unit tests**

**Step 2: Session create stores ids; spawn merges config**

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): bind catalog MCP ids into session config"
```

---

### Task 6: HTTP API — personal + admin controllers

**Files:**
- Create: `backend/Controllers/McpServersController.cs`
- Create: `backend/Controllers/AdminMcpServersController.cs`
- Create: `tests/AgentHub.Api.Tests/LibraryCoreControllersTests.cs`
- Modify: `frontend/src/api.js` — client methods (can wait until Task 9 if preferred; backend-first here)

**Endpoints:**
- `GET/POST /api/mcp-servers`, `PUT/DELETE /api/mcp-servers/{id}`
- `GET/POST/PUT/DELETE /api/admin/mcp-servers` — forces `owner=__org__`, admin-only
- `POST /api/mcp-servers/from-api` — body `{ name, description?, specUrl, specType?, baseUrl?, auth?, secret?, save: true }` creates `kind=api` (spec fetch validation can be shallow until Task 8: URL + JSON/SDL sniff)

**Step 1: Controller tests with fakes**

**Step 2: Implement**

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): personal and admin MCP server APIs"
```

---

### Task 7: Secret encryption + hybrid rules

**Files:**
- Create: `backend/Library/McpSecretProtector.cs` (Data Protection purpose `mcp-server-secrets`)
- Modify: `McpServerStore` to protect/unprotect `secret_json`
- Modify: `McpServerInfo` / controllers — never return secrets; optional `hasSecret: bool`
- Tests: protector round-trip; non-owner list omits config; org secret not in API responses

**Step 1: Failing tests**

**Step 2: Implement hybrid rules documented in design**

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): encrypt MCP secrets and hide from non-owners"
```

---

### Task 8: API→MCP HTTP gateway (OpenAPI)

**Files:**
- Create: `mcp/api-gateway/` (Node: `@modelcontextprotocol/sdk`, express/hono) **or** `backend/Library/ApiMcpGateway/` if C#
- Create: fixture `tests/fixtures/openapi/petstore-mini.json`
- Create: gateway unit tests (Node `node --test` or xUnit for C#)
- Modify: API reverse-proxy / map `/mcp/api/{id}` and auth middleware
- Modify: `McpConfigAssembler` to emit real internal URL + `headers` with spawn token
- Create: `backend/Library/McpGatewayTokenService.cs` — issue/validate session-bound tokens

**Behavior:**
- Load catalog `api` entry by id; verify token allows it
- Fetch/cache OpenAPI; map operations → tools
- Proxy HTTP calls with bearer/header auth from decrypted secret when allowed

**Step 1: Tool generation test against petstore-mini**

**Step 2: Auth reject without token**

**Step 3: Wire assembler URLs for agent pods (cluster-internal base URL from config `McpGateway:PublicBaseUrl` / in-cluster URL)

**Step 4: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): OpenAPI HTTP MCP gateway"
```

---

### Task 9: GraphQL support + ephemeral session sources

**Files:**
- Extend gateway for GraphQL introspection / schema URL
- Create: `backend/Library/EphemeralApiMcpStore.cs` (memory + optional PG) keyed by `sessionId` + name
- Modify: `CreateSessionRequest` — `EphemeralApiSources[]`
- Route: `/mcp/session/{sessionId}/{name}`
- Delete registrations on session delete

**Step 1: Tests for GraphQL tool list from fixture schema**

**Step 2: Tests for ephemeral register + token + cleanup**

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): GraphQL gateway and ephemeral session API MCPs"
```

---

### Task 10: Settings UI — personal MCP pane

**Files:**
- Create: `frontend/src/components/McpServersPane.vue` (port/adapt WIP; add From API / Raw tabs, kind badge, hasSecret)
- Modify: `frontend/src/components/SettingsView.vue` — PERSONAL tab “MCP servers”
- Modify: `frontend/src/api.js`
- Create: `frontend/src/components/library-panes.test.js` (MCP parts only from WIP)

**Step 1: Vitest for create raw + create from-api**

**Step 2: Implement pane**

**Step 3: `cd frontend && npm test -- library-panes` — PASS; commit**

```bash
git commit -m "feat(mcp-catalog): settings UI for personal MCP library"
```

---

### Task 11: Settings UI — admin org catalog + EE share controls

**Files:**
- Modify: `McpServersPane.vue` or `AdminMcpServersPane.vue` for org mode
- Create: `frontend/src/components/LibraryShareControls.vue` (port WIP)
- Modify: `SettingsView.vue` — ADMIN tab
- Modify: EE API client for shares
- Tests: share controls + admin create

**Step 1: Failing vitest**

**Step 2: Implement**

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): admin org MCP catalog and EE share UI"
```

---

### Task 12: Session dialogs — picker + URL paste

**Files:**
- Modify: `frontend/src/components/NewSessionDialog.vue`
- Modify: `frontend/src/components/EditSessionDialog.vue`
- Modify: `frontend/src/components/DuplicateSessionDialog.vue` (include MCP ids option if WIP does)
- Modify: `frontend/src/lib/agent.js` if defaults needed
- Create: `frontend/src/components/session-mcp-picker.test.js` (port WIP + ephemeral URL cases)

**UI in Advanced:**
1. Multi-select picker from `api.mcpServers()`
2. API URL field + optional “Save to my library”
3. Existing MCP JSON textarea

**Submit payload:**
```js
{
  mcpServerIds: [...],
  ephemeralApiSources: url ? [{ name, specUrl, specType: 'auto', saveToLibrary: bool }] : [],
  mcpConfigJson: ...
}
```

**Step 1: Vitest from WIP + new ephemeral cases**

**Step 2: Implement**

**Step 3: PASS + commit**

```bash
git commit -m "feat(mcp-catalog): select and paste API MCPs in session dialogs"
```

---

### Task 13: End-to-end wiring + verification

**Files:**
- Modify: deploy/helm or config samples only if required for `McpGateway:BaseUrl` (prefer gitignored deploy; document env in README snippet under existing docs — only if project already documents similar knobs)
- Run full: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`
- Run: `cd frontend && npm test`
- Run gateway tests if Node

**Step 1: Fix regressions**

**Step 2: Manual checklist**
- [ ] Create personal raw MCP → appear in New Session picker → session HasMcp
- [ ] Create org OpenAPI MCP → visible to another user (no EE)
- [ ] With EE: restrict org MCP to group → unauthorized user cannot select
- [ ] Paste URL in Advanced without save → ephemeral only
- [ ] Agent tool list shows generated OpenAPI tools (dev cluster)

**Step 3: Commit any fixes**

```bash
git commit -m "fix(mcp-catalog): harden gateway wiring and access edge cases"
```

---

## Execution notes

- Prefer copying MCP-only files from `.worktrees/skill-library-mcp` then adapting over rewriting from scratch.
- Do not merge skills code.
- No company or internal-environment references.
- Commit after each task; do not push unless asked.
- TDD: red → green → commit per task.
