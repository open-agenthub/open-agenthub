# MCP Session Orchestration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Let external AIs and in-pod agents create, list, get, wait on, and delete Open AgentHub sessions via MCP for team orchestration.

**Architecture:** Shared `ISessionService` create/delete path. External stdio MCP (`mcp/agenthub`) uses Bearer `oah_…` against `/api/remote`. In-pod stdio MCP (`agenthub_sessions`, browser-MCP pattern) uses `X-Agent-Token` against `/internal/sessions/{parentId}/…`. Persist optional `parent_session_id`; soft-limit running sessions per owner; in-pod delete only descendants.

**Tech Stack:** ASP.NET Core, Postgres/Npgsql, Node MCP SDK + zod, Vue settings snippet, Helm configmap values.

**Spec:** `docs/superpowers/specs/2026-07-27-mcp-session-orchestration-design.md`

---

### Task 1: Persist `parent_session_id`

**Files:**
- Modify: `backend/Persistence/PostgresSessionStore.cs`
- Modify: `backend/Models/SessionModels.cs` (`CreateSessionRequest`, `SessionInfo`)
- Modify: `backend/Services/KubernetesSessionService.cs` (`CreateSessionCoreAsync`, `ToInfo`)
- Test: `tests/AgentHub.Api.Tests/SessionParentTests.cs` (create)

**Step 1: Write the failing test**

```csharp
public class SessionParentTests
{
    [Fact]
    public void CreateSessionRequest_AcceptsParentSessionId()
    {
        var req = new CreateSessionRequest
        {
            Title = "child",
            Mode = SessionMode.Autonomous,
            Prompt = "do work",
            ParentSessionId = "parent-1"
        };
        Assert.Equal("parent-1", req.ParentSessionId);
    }
}
```

Also add a store-level or service-mapping assertion once `SessionRecord.ParentSessionId` exists (extend an existing session store test pattern if present; otherwise unit-test `ToInfo` via creating a record in a focused helper).

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~SessionParentTests"`

Expected: FAIL (missing `ParentSessionId`).

**Step 3: Minimal implementation**

- `SessionRecord.ParentSessionId` (`string?`)
- `CreateSessionRequest.ParentSessionId` (`string?`)
- `SessionInfo.ParentSessionId` (`string?`)
- DDL: `ALTER TABLE sessions ADD COLUMN IF NOT EXISTS parent_session_id TEXT;`
- Index: `CREATE INDEX IF NOT EXISTS idx_sessions_parent ON sessions(owner, parent_session_id);`
- Upsert + SELECT columns include `parent_session_id`
- `CreateSessionCoreAsync` copies `req.ParentSessionId` onto the record
- `ToInfo` maps `ParentSessionId = r.ParentSessionId`

**Step 4: Run tests — expect PASS**

**Step 5: Commit**

```bash
git add backend/Persistence/PostgresSessionStore.cs backend/Models/SessionModels.cs backend/Services/KubernetesSessionService.cs tests/AgentHub.Api.Tests/SessionParentTests.cs
git commit -m "feat(sessions): persist optional parentSessionId"
```

---

### Task 2: Soft-limit running sessions per owner

**Files:**
- Modify: `backend/Services/KubernetesSessionService.cs` (or small helper)
- Modify: `backend/appsettings.json` — `AgentHub:MaxRunningSessionsPerOwner` (default `20`)
- Modify: Helm `values.yaml` + `templates/configmap.yaml`
- Test: `tests/AgentHub.Api.Tests/SessionSoftLimitTests.cs`

**Step 1: Failing test**

Fake store returning N sessions with statuses in `Pending|Running|Paused|Scheduled`. Assert create throws / returns limit error when count >= configured max. Prefer a dedicated exception e.g. `SessionLimitExceededException` mapped to `429` in controllers.

**Step 2: Run — expect FAIL**

**Step 3: Implement**

- Before create in `CreateSessionCoreAsync`, count owner's sessions whose `Status` is in the running set (use store list or add `CountRunningAsync`).
- If `count >= max` throw `SessionLimitExceededException`.
- Wire config `cfg.GetValue("AgentHub:MaxRunningSessionsPerOwner", 20)` (treat `<= 0` as unlimited only if you document it; prefer require positive int, default 20).

**Step 4: PASS + commit**

```bash
git commit -m "feat(sessions): enforce max running sessions per owner"
```

---

### Task 3: Remote DELETE + map soft-limit to 429

**Files:**
- Modify: `backend/Controllers/RemoteController.cs`
- Modify: `backend/Controllers/SessionsController.cs` (map limit → 429 for UI consistency)
- Test: `tests/AgentHub.Api.Tests/RemoteControllerTests.cs` (create if missing)

**Step 1: Failing tests**

- `DELETE /api/remote/sessions/{id}` with valid owner token → calls `DeleteSessionAsync`
- Invalid token → 401
- Create when limit exceeded → 429

**Step 2: FAIL, then implement**

```csharp
[HttpDelete("sessions/{id}")]
public async Task<IActionResult> Delete(string id, CancellationToken ct)
{
    var owner = await ResolveOwnerAsync(ct);
    if (owner is null) return Unauthorized();
    try
    {
        await _svc.DeleteSessionAsync(owner, id, ct);
        return NoContent();
    }
    catch (KeyNotFoundException) { return NotFound(); }
}
```

Catch `SessionLimitExceededException` on Create → `StatusCode(429, e.Message)`.

**Step 3: PASS + commit**

```bash
git commit -m "feat(remote): delete sessions and return 429 on soft limit"
```

---

### Task 4: Descendant helpers + internal spawn/peer API

**Files:**
- Create: `backend/Services/SessionDescent.cs` (pure helpers)
- Modify: `backend/Controllers/InternalController.cs`
- Modify: `backend/Persistence/PostgresSessionStore.cs` — `ListByParentAsync` optional
- Test: `tests/AgentHub.Api.Tests/SessionDescentTests.cs`
- Test: `tests/AgentHub.Api.Tests/InternalSessionSpawnTests.cs`

**Step 1: Pure helper tests**

```csharp
[Fact]
public void IsDescendant_WalksParentChain()
{
    var byId = new Dictionary<string, string?>
    {
        ["root"] = null,
        ["a"] = "root",
        ["b"] = "a",
        ["other"] = null
    };
    Assert.True(SessionDescent.IsDescendant("b", "root", id => byId.GetValueOrDefault(id)));
    Assert.False(SessionDescent.IsDescendant("other", "root", id => byId.GetValueOrDefault(id)));
}
```

Guard against cycles (max hops e.g. 64 → false).

**Step 2: Internal controller tests with fakes**

- Spawn with matching callback token + parent id → `CreateSessionAsync(owner, req with ParentSessionId=parentId)`
- Wrong token / wrong parent id → 401
- Get/Delete peer when descendant → ok; sibling → 404
- Soft limit → 429

**Step 3: Implement routes on `InternalController`**

```
POST   /internal/sessions/{id}/spawn
GET    /internal/sessions/{id}/children
GET    /internal/sessions/{id}/peer/{childId}
DELETE /internal/sessions/{id}/peer/{childId}
```

Auth: existing `AuthAsync(id, ct)` (token must equal path `id`).

Spawn: force `ParentSessionId` to `id` (ignore client override that escapes this parent). Owner = parent.Owner.

Children: list owner sessions where `ParentSessionId == id` (direct children is enough for list; get/delete use full descendant walk).

**Step 4: PASS + commit**

```bash
git commit -m "feat(internal): spawn and manage descendant sessions"
```

---

### Task 5: In-pod sessions MCP client + server

**Files:**
- Create: `agent-runtime/sessions/client.mjs`
- Create: `agent-runtime/sessions/server.mjs`
- Create: `agent-runtime/sessions/wait.mjs`
- Test: `agent-runtime/session-agent/test/sessions-client.test.js`
- Test: `agent-runtime/session-agent/test/sessions-wait.test.js`
- Test: `agent-runtime/session-agent/test/sessions-server.test.js` (optional smoke registering tools)

**Step 1: Failing client tests** (mirror `browser/client.mjs`)

Env: `AGENTHUB_CALLBACK_URL`, `AGENTHUB_CALLBACK_TOKEN`, `AGENTHUB_SESSION_ID`.

Methods: `create(body)`, `get(childId)`, `listChildren()`, `delete(childId)` →  
`{callback}/sessions/{sessionId}/spawn|children|peer/{id}` with `X-Agent-Token`, 64 KiB bound, stable error codes, never include token in thrown messages.

**Step 2: Wait helper tests**

```js
// fake get that returns Running then Succeeded
const result = await waitForSession(get, 'c1', { intervalMs: 1, timeoutMs: 50 });
assert.equal(result.phase, 'Succeeded');
assert.equal(result.timedOut, false);
```

Timeout case → `{ timedOut: true, phase, id }`.

**Step 3: Implement `server.mjs`**

```js
const server = new McpServer({ name: 'agenthub_sessions', version: '1.0.0' });
server.registerTool('session_create', { /* zod schema */ }, async (args) => text(await client.create(args)));
// session_get, session_list, session_wait, session_delete
```

Default create: `mode: 'Autonomous'`, omit secrets from responses (pass through SessionInfo JSON only).

**Step 4: `npm test` in `agent-runtime/session-agent` — PASS + commit**

```bash
git commit -m "feat(runtime): add agenthub_sessions MCP tools"
```

---

### Task 6: Inject `agenthub_sessions` into provider MCP config

**Files:**
- Modify: `agent-runtime/browser/configure-claude.mjs` **or** create `agent-runtime/sessions/configure.mjs` and call from entrypoints (prefer dedicated `sessions/configure.mjs` merged alongside browser)
- Modify: `agent-runtime/common/entrypoint-common.sh`
- Modify: `agent-runtime/codex/entrypoint.sh` / `codex/mcp-config.js` as needed
- Modify: Cursor MCP merge path (same pattern as browser)
- Modify: `backend/Services/AgentPodSpecFactory.cs` — `AGENTHUB_SPAWN_MCP_ENABLED`
- Modify: Dockerfiles that `COPY` browser dir — also copy `sessions/`
- Test: `agent-runtime/session-agent/test/sessions-mcp-config.test.js`

**Step 1: Failing config tests**

When enabled, Claude merge includes `agenthub_sessions` command `node` + `…/sessions/server.mjs`, overwriting user collision. Codex toml appends `[mcp_servers.agenthub_sessions]`. When env `0`, not injected.

**Step 2: Implement injection gated by `AGENTHUB_SPAWN_MCP_ENABLED=1`**

Wire from `AgentHub:SpawnMcpEnabled` (default `true`) into pod env like browser.

**Step 3: PASS + commit**

```bash
git commit -m "feat(runtime): auto-inject agenthub_sessions MCP when enabled"
```

---

### Task 7: External MCP package `mcp/agenthub`

**Files:**
- Create: `mcp/agenthub/package.json`
- Create: `mcp/agenthub/server.mjs`
- Create: `mcp/agenthub/client.mjs`
- Create: `mcp/agenthub/wait.mjs` (shared logic or duplicate thin wait)
- Create: `mcp/agenthub/test/*.test.js`
- Optional: root README blurb / Settings snippet only in Task 8

**Step 1: Failing tests** against fake HTTP server

- Bearer `Authorization: Bearer oah_…`
- `POST/GET/DELETE /api/remote/sessions`, list, wait polling

**Step 2: Implement stdio MCP** with same tool names as in-pod.

Config: `AGENTHUB_URL`, `AGENTHUB_TOKEN`.

**Step 3: `npm test` in `mcp/agenthub` — PASS + commit**

```bash
git commit -m "feat(mcp): add external agenthub session orchestration MCP"
```

---

### Task 8: Settings docs + Helm wiring

**Files:**
- Modify: `frontend/src/components/SettingsDialog.vue` — MCP config snippet under API tokens
- Modify: `helm/open-agenthub/values.yaml`, `templates/configmap.yaml`, `_helpers.tpl` if needed
- Modify: `backend/appsettings.json`
- Modify: `README.md` short subsection (Bring your tools / Remote API)
- Test: light frontend test if settings snippets are asserted elsewhere; else manual

**Step 1: Add values**

```yaml
spawnMcp:
  enabled: true
maxRunningSessionsPerOwner: 20
```

Map to `AgentHub__SpawnMcpEnabled` and `AgentHub__MaxRunningSessionsPerOwner`.

**Step 2: Settings snippet**

```json
{
  "mcpServers": {
    "agenthub": {
      "command": "npx",
      "args": ["-y", "..."],
      "env": {
        "AGENTHUB_URL": "<origin>",
        "AGENTHUB_TOKEN": "<token>"
      }
    }
  }
}
```

Until published to npm, document local path: `node /path/to/repo/mcp/agenthub/server.mjs`.

**Step 3: Commit**

```bash
git commit -m "docs: wire spawn MCP settings and Helm limits"
```

---

### Task 9: Verification sweep

**Step 1: Backend tests**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "FullyQualifiedName~SessionParent|FullyQualifiedName~SessionSoftLimit|FullyQualifiedName~SessionDescent|FullyQualifiedName~InternalSessionSpawn|FullyQualifiedName~RemoteController"
```

Expected: PASS

**Step 2: Runtime + MCP tests**

```powershell
cd agent-runtime/session-agent; npm test
cd ../../mcp/agenthub; npm test
```

Expected: PASS

**Step 3: Final commit if docs/fixes remain**

```bash
git commit -m "test: cover MCP session orchestration end-to-end units"
```

---

## Execution notes

- Follow TDD per task; do not skip failing-test steps.
- Never mount personal `oah_` tokens into pods.
- Never log or return callback/API tokens from MCP tools.
- Prefer mirroring `agent-runtime/browser/*` patterns for client bounds and error codes.
- YAGNI: no pause/resume MCP, no cascade delete, no orchestration UI badge in this plan.
