# OpenClaw Agent Support + EE Allowed Agents Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add OpenClaw as a CE first-class session agent (Interactive TUI / Autonomous+Scheduled `--local`) with Subscription or reused ApiKey billing, plus an EE admin whitelist of allowed agent kinds.

**Architecture:** Mirror Cursor: fourth runtime image `agent-runtime-openclaw` with a provider driver over shared PTY transport. Persist `OpenClaw` + optional `OpenClawApiKeySource` on the session. ApiKey mode mounts only the chosen existing key (Anthropic/OpenAI/Cursor); Subscription uses `openclaw-{owner}` Secret + watcher. EE adds `IAllowedAgentsProvider` (CE default = all) with Postgres store + admin UI gated by license.

**Tech Stack:** .NET 10 / ASP.NET Core, KubernetesClient, PostgreSQL/Npgsql, Node.js (agent-runtime), Vue 3/Vite/Vitest, Docker, Helm 3.

**Design:** `docs/superpowers/specs/2026-07-27-openclaw-agent-support-design.md`

## Global Constraints

- Never mention unrelated company brands in code, comments, commits, READMEs, configs, samples, or docs. Author/contact: Maik Boltze / `open-agenthub` / `open-agenthub@mail.on-mb.com` only (`AGENTS.md`).
- Never log, return, fixture, or commit real API keys, tokens, or credentials. Use unmistakably synthetic fixtures only.
- OpenClaw is a peer to Claude/Codex/Cursor: shared transport in `agent-runtime/common/`; provider code only under `agent-runtime/openclaw/`.
- Pod receives only the credential selected by `Agent` + `AuthMode` (+ `OpenClawApiKeySource` when ApiKey). Subscription never injects API keys; ApiKey never mounts the OpenClaw subscription Secret.
- OpenClaw never uses Claude-only `AgentAuthMode.Auto`.
- CE / unlicensed EE: all agents allowed. Licensed EE without stored config: all allowed. Restriction is opt-in.
- OpenClaw CLI contracts below are starting pins; discover exact auth file shape during the runtime task and update validators before merge.
- TDD: red → green → refactor; each task ends with independently passing focused tests.
- On Windows use PowerShell-friendly `dotnet test` / `npm test`.

### OpenClaw CLI pins (driver + Dockerfile)

| Fact | Contract |
| --- | --- |
| Binary | `openclaw` (npm global or image PATH) |
| Interactive | `openclaw tui --local` (aliases: `openclaw chat` / `openclaw terminal`) |
| Autonomous / Scheduled | `openclaw agent --local --agent <id> --message <prompt>` (or `--message-file`) |
| Resume | Prefer `--session-id` / restored `~/.openclaw` state; one fresh-session fallback when missing |
| ApiKey env (by source) | Anthropic → `ANTHROPIC_API_KEY`; OpenAI → `OPENAI_API_KEY`; Cursor → `CURSOR_API_KEY` |
| Subscription | Interactive login / `openclaw models auth …`; watch credential file under `~/.openclaw` |
| State dir | `$HOME/.openclaw` (exclude credential files from state tar) |

### Placeholder subscription credential shape (replace after discovery)

Until inspected in the runtime task:

- Secret file key: `auth.json` (adjust if discovery differs)
- Placeholder marker (synthetic only):

```json
{"openclawAuth":{"accessToken":"synthetic-test-token-not-real"}}
```

Update `ProviderCredentialValidator`, watcher, and tests after discovery. Do not invent extra fields beyond the discovered store.

---

## File map

- `backend/Models/SessionModels.cs` — `AgentKind.OpenClaw`, `OpenClawApiKeySource`, validation
- `backend/Persistence/PostgresSessionStore.cs` — `openclaw_api_key_source` column + `SessionRecord` field
- `backend/Services/ProviderCredentialValidator.cs` — OpenClaw subscription JSON
- `backend/Services/CredentialSecretFactory.cs` — status `openclawSubscription`; provider file key
- `backend/Services/KubernetesSessionService.cs` — Secret name, image option, status merge, pod context
- `backend/Services/AgentPodSpecFactory.cs` — OpenClaw image + credential mounts by auth/source
- `backend/Storage/S3ArtifactStore.cs` — `openclaw-state.tgz`
- `backend/Controllers/SessionsController.cs` / create path — allowlist check hook
- `backend/Agents/IAllowedAgentsProvider.cs` (+ CE default) — core interface
- `ee/backend/Agents/` — store, EE provider, admin controller
- `agent-runtime/openclaw/` — Dockerfile, entrypoint, driver, auth-watcher, mcp/policy as needed
- `frontend/src/lib/agent.js` — OpenClaw option, api-key source helpers, allowlist filter
- `frontend/src/components/*Session*.vue`, `CredentialsDialog.vue`, `AdminLimitsView.vue` / new allowlist panel
- `helm/`, `k8s/20-backend.yaml`, `setup-dev.*`, CI workflows, README, acceptance doc

---

### Task 1: Accept `AgentKind.OpenClaw` and `OpenClawApiKeySource` validation

**Files:**
- Modify: `backend/Models/SessionModels.cs`
- Modify: `tests/AgentHub.Api.Tests/SessionAgentModelTests.cs`
- Modify: `tests/AgentHub.Api.Tests/SessionDuplicationTests.cs`

**Step 1: Write the failing test**

```csharp
public enum OpenClawApiKeySource { Anthropic, OpenAI, Cursor } // expected addition

[Fact]
public void CreateConfiguration_AcceptsOpenClawSubscriptionAndApiKey()
{
    AgentConfiguration.ValidateForCreate(AgentKind.OpenClaw, AgentAuthMode.Subscription);
    AgentConfiguration.ValidateForCreate(AgentKind.OpenClaw, AgentAuthMode.ApiKey,
        OpenClawApiKeySource.Anthropic);
}

[Fact]
public void CreateConfiguration_RejectsOpenClawAuto()
{
    Assert.Throws<ArgumentException>(() =>
        AgentConfiguration.ValidateForCreate(AgentKind.OpenClaw, AgentAuthMode.Auto));
}

[Fact]
public void CreateConfiguration_RequiresApiKeySourceForOpenClawApiKey()
{
    Assert.Throws<ArgumentException>(() =>
        AgentConfiguration.ValidateForCreate(AgentKind.OpenClaw, AgentAuthMode.ApiKey, null));
}

[Fact]
public void CreateConfiguration_RejectsApiKeySourceUnlessOpenClawApiKey()
{
    Assert.Throws<ArgumentException>(() =>
        AgentConfiguration.ValidateForCreate(AgentKind.Claude, AgentAuthMode.ApiKey,
            OpenClawApiKeySource.OpenAI));
}
```

Extend `ValidateForCreate` / update / duplicate signatures to accept optional `OpenClawApiKeySource?`. Duplicate must copy source when present.

**Step 2: Run test to verify it fails**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "SessionAgentModelTests|SessionDuplicationTests" --nologo
```

Expected: compile failures (`OpenClaw` / `OpenClawApiKeySource` missing) or validation rejections.

**Step 3: Write minimal implementation**

```csharp
public enum AgentKind { Claude, Codex, Cursor, OpenClaw }
public enum OpenClawApiKeySource { Anthropic, OpenAI, Cursor }

// ValidateAgent accepts OpenClaw
// ValidateForCreate(agent, authMode, openClawApiKeySource = null):
//   if OpenClaw + ApiKey => source required and must be defined enum
//   if source != null && !(OpenClaw && ApiKey) => throw
```

Add `OpenClawApiKeySource?` to `CreateSessionRequest`, `UpdateSessionRequest`, and duplication copy path.

**Step 4: Run tests to verify they pass**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "SessionAgentModelTests|SessionDuplicationTests" --nologo
```

**Step 5: Commit**

```powershell
git add backend/Models/SessionModels.cs tests/AgentHub.Api.Tests/SessionAgentModelTests.cs tests/AgentHub.Api.Tests/SessionDuplicationTests.cs
git commit -m "feat: accept OpenClaw agent kind and API key source"
```

---

### Task 2: Persist `openclaw_api_key_source` on sessions

**Files:**
- Modify: `backend/Persistence/PostgresSessionStore.cs`
- Modify: `tests/AgentHub.Api.Tests/` (session store Postgres test if present; else unit bind coverage via existing upsert tests)
- Wire create/update/duplicate in `KubernetesSessionService` / sessions controller mappings

**Step 1: Write the failing test**

Assert upsert + get round-trips `OpenClawApiKeySource.OpenAI` for an OpenClaw ApiKey session; null for Claude.

**Step 2: Run test — expect RED** (column / field missing)

**Step 3: Minimal implementation**

```sql
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS openclaw_api_key_source TEXT;
```

Map nullable enum as TEXT (`Anthropic`/`OpenAI`/`Cursor`/NULL). Include in INSERT/UPDATE/SELECT/Bind.

**Step 4: GREEN** focused store tests

**Step 5: Commit**

```powershell
git commit -m "feat: persist OpenClaw API key source on sessions"
```

---

### Task 3: OpenClaw subscription Secret + placeholder validator

**Files:**
- Modify: `backend/Services/ProviderCredentialValidator.cs`
- Modify: `backend/Services/CredentialSecretFactory.cs`
- Modify: `backend/Services/KubernetesSessionService.cs` (`ProviderSecretName`, credential status)
- Modify: `backend/Controllers/InternalController.cs` (agent route mapping for `openclaw`)
- Modify: related tests (`ProviderCredentialValidatorTests`, `CredentialSelectionTests`, `CredentialSecretFactoryTests`)

**Step 1: Failing tests**

```csharp
[InlineData(AgentKind.OpenClaw, "{\"openclawAuth\":{\"accessToken\":\"x\"}}", true)]
[InlineData(AgentKind.OpenClaw, "{\"tokens\":{}}", false)]
[InlineData(AgentKind.OpenClaw, "{}", false)]
```

```csharp
Assert.Equal("openclaw-u-2bd806c97f0e00af",
    KubernetesSessionService.ProviderSecretName("alice", AgentKind.OpenClaw));
Assert.True(status.OpenclawSubscription); // when secret present
```

Internal PUT `.../openclaw-credentials` only for OpenClaw + Subscription.

**Step 2: RED**

**Step 3: Implement** validator branch, Secret name `openclaw-{Sanitize(owner)}`, status bool, factory file key placeholder `auth.json`.

**Step 4: GREEN**

**Step 5: Commit**

```powershell
git commit -m "feat: store OpenClaw subscription credentials"
```

---

### Task 4: Pod spec — OpenClaw image and credential selection

**Files:**
- Modify: `backend/Services/AgentPodSpecFactory.cs` (`AgentRuntimeImages.OpenClawImage`, `PodBuildContext.OpenClawCredentialSecretName`)
- Modify: `backend/Services/KubernetesSessionService.cs` (`OpenClawAgentImage`, build context, `HasSelectedApiKey` by source)
- Modify: `backend/appsettings.json`
- Modify: `tests/AgentHub.Api.Tests/AgentPodSpecFactoryTests.cs`
- Modify: `backend/Storage/S3ArtifactStore.cs` → `openclaw-state.tgz` (+ `ArtifactStoreKeyTests`)

**Step 1: Failing tests**

```csharp
[InlineData(AgentKind.OpenClaw, AgentAuthMode.Subscription, "subscription", "runtime-openclaw", "openclaw", null)]
[InlineData(AgentKind.OpenClaw, AgentAuthMode.ApiKey, "apikey-anthropic", "runtime-openclaw", null, "ANTHROPIC_API_KEY")]
[InlineData(AgentKind.OpenClaw, AgentAuthMode.ApiKey, "apikey-openai", "runtime-openclaw", null, "OPENAI_API_KEY")]
[InlineData(AgentKind.OpenClaw, AgentAuthMode.ApiKey, "apikey-cursor", "runtime-openclaw", null, "CURSOR_API_KEY")]
```

Assert Subscription mounts only OpenClaw secret; ApiKey mounts only selected key env and never OpenClaw subscription volume. Assert missing-credential diagnostic for Autonomous without key.

**Step 2: RED**

**Step 3: Implement** image switch + mount/env logic keyed by `OpenClawApiKeySource`. Extend `MissingCredentialDiagnostic` for OpenClaw.

**Step 4: GREEN**

**Step 5: Commit**

```powershell
git commit -m "feat: mount OpenClaw runtime image and selected credentials"
```

---

### Task 5: OpenClaw agent runtime image + driver

**Files:**
- Create: `agent-runtime/openclaw/Dockerfile`
- Create: `agent-runtime/openclaw/entrypoint.sh`
- Create: `agent-runtime/openclaw/driver.js`
- Create: `agent-runtime/openclaw/auth-watcher.js`
- Create: `agent-runtime/session-agent/test/openclaw-driver.test.js` (+ auth-watcher test)
- Modify: setup/CI as needed for local builds (full helm in Task 7)

**Step 1: Failing driver tests**

Assert Interactive builds `openclaw tui --local` (or pinned equivalent); Autonomous builds `openclaw agent --local ... --message ...`; resume uses session id when set; `prepare` scrubs unselected keys; subscription watcher uploads on file change.

**Step 2: RED**

**Step 3: Implement** Dockerfile installing `openclaw` at a pinned version; driver implementing `driver-contract.js`; auth watcher mirroring Cursor pattern with OpenClaw paths. Discover real auth filename/JSON; update placeholder validator from Task 3 if needed (same commit or follow-up in this task).

**Step 4: GREEN**

```powershell
cd agent-runtime/session-agent; npm test -- openclaw
```

**Step 5: Commit**

```powershell
git commit -m "feat: add OpenClaw agent runtime image and driver"
```

---

### Task 6: Frontend — OpenClaw in session + credentials UI

**Files:**
- Modify: `frontend/src/lib/agent.js` (+ existing agent tests)
- Modify: `frontend/src/components/NewSessionDialog.vue`, `EditSessionDialog.vue`, `DuplicateSessionDialog.vue`, `AgentDecisionCard.vue`
- Modify: `frontend/src/components/CredentialsDialog.vue` (show OpenClaw subscription status only; no new API key fields)
- Modify: list/detail/search displays if they special-case agents

**Step 1: Failing Vitest**

- `agentOptions` includes OpenClaw
- ApiKey + OpenClaw shows source selector (Anthropic/OpenAI/Cursor)
- Payload includes `openClawApiKeySource` only when needed
- `credentialReadiness` for OpenClaw Subscription / ApiKey+source
- Credentials dialog shows subscription boolean, not a new key input

**Step 2: RED**

**Step 3: Implement** helpers + dialog bindings; default policy for OpenClaw (start with Claude-like or empty allowlists — keep minimal).

**Step 4: GREEN**

```powershell
cd frontend; npm test -- agent
```

**Step 5: Commit**

```powershell
git commit -m "feat: expose OpenClaw in session and credentials UI"
```

---

### Task 7: Helm, manifests, CI, docs

**Files:**
- Modify: `helm/open-agenthub/values.yaml`, `values-dev.yaml`, `templates/configmap.yaml`, helpers
- Modify: `k8s/20-backend.yaml`
- Modify: `setup-dev.ps1`, `setup-dev.sh`
- Modify: `.github/workflows/build-images.yml`, `test.yml` as needed
- Modify: `tests/helm/*.ps1` parity scripts
- Modify: `README.md`
- Create: `docs/testing/openclaw-docker-desktop-acceptance.md`

**Step 1: Failing helm parity assertions** for `agent-runtime-openclaw` / `OpenClawAgentImage`

**Step 2: RED**

**Step 3: Wire image everywhere Cursor is wired**

**Step 4: GREEN** helm scripts + doc sketch

**Step 5: Commit**

```powershell
git commit -m "chore: ship OpenClaw image through Helm, CI, and docs"
```

---

### Task 8: CE `IAllowedAgentsProvider` + EE store/API

**Files:**
- Create: `backend/Agents/IAllowedAgentsProvider.cs`
- Create: `backend/Agents/AllowAllAgentsProvider.cs`
- Create: `ee/backend/Agents/AllowedAgentsStore.cs`
- Create: `ee/backend/Agents/EeAllowedAgentsProvider.cs`
- Create: `ee/backend/Agents/AllowedAgentsAdminController.cs`
- Modify: `backend/Program.cs` — register CE default; replace with EE provider when EE assembly linked (same pattern as usage limits)
- Create: `tests/AgentHub.Api.Tests/AllowedAgentsTests.cs`
- Modify: `ee/README.md` — document feature

**Step 1: Failing tests**

```csharp
[Fact]
public async Task CeProvider_AllowsAllKnownAgents()
{
    var p = new AllowAllAgentsProvider();
    var allowed = await p.GetAllowedAsync(CancellationToken.None);
    Assert.Contains(AgentKind.OpenClaw, allowed);
    Assert.Equal(Enum.GetValues<AgentKind>().Length, allowed.Count);
}

[Fact]
public async Task EeProvider_WithoutConfig_AllowsAll_WhenLicensed()
{
    // licensed mock + empty store => all
}

[Fact]
public async Task EeProvider_WithWhitelist_Restricts()
{
    // store { Claude, OpenClaw } => only those; IsAllowed(Cursor) false
}

[Fact]
public async Task EeProvider_Unlicensed_AllowsAll()
{
    // ignore store when license inactive
}
```

Admin GET/PUT `/api/admin/allowed-agents` — Forbid non-admin; 402 or allow-all semantics without license consistent with other EE admin APIs.

**Step 2: RED**

**Step 3: Implement**

```csharp
public interface IAllowedAgentsProvider
{
    Task<IReadOnlyList<AgentKind>> GetAllowedAsync(CancellationToken ct = default);
    Task<bool> IsAllowedAsync(AgentKind agent, CancellationToken ct = default);
}
```

Store table e.g. `allowed_agents (agent TEXT PRIMARY KEY)` — empty table means “no restriction” (all allowed). PUT replaces full set; reject empty set if product should require ≥1 agent (recommend: empty PUT = clear restriction / all allowed; non-empty = exact whitelist).

**Step 4: GREEN**

**Step 5: Commit**

```powershell
git commit -m "feat(ee): add admin-managed allowed agent kinds"
```

---

### Task 9: Enforce allowlist on create/update/start/resume/duplicate

**Files:**
- Modify: `backend/Controllers/SessionsController.cs` and/or `KubernetesSessionService.cs`
- Modify: public sessions bootstrap if needed (`GET` allowed agents for non-admins)
- Modify: tests for 403 paths

**Step 1: Failing tests**

Create OpenClaw when whitelist is `{Claude}` → 403. Start/resume existing OpenClaw session when later disallowed → 403. Duplicate onto Cursor when disallowed → 403. Update agent to disallowed → 403. CE provider → success.

**Step 2: RED**

**Step 3: Implement** single helper `EnsureAgentAllowedAsync(agent)` called from create/duplicate/update/start/resume. Add `GET /api/agents/allowed` (authorized user) returning current list for UI filtering.

**Step 4: GREEN**

**Step 5: Commit**

```powershell
git commit -m "feat: enforce allowed agent kinds on session lifecycle"
```

---

### Task 10: Frontend — admin allowlist UI + selector filtering

**Files:**
- Modify: `frontend/src/api.js`
- Modify: `frontend/src/components/AdminLimitsView.vue` or Settings admin section (add “Allowed agents” panel)
- Modify: `frontend/src/lib/agent.js` — `filterAgentOptions(allowed)`
- Modify: session dialogs to load allowed agents and filter
- Add: Vitest coverage

**Step 1: Failing tests** for admin checkboxes save/load and New Session hiding disallowed agents

**Step 2: RED**

**Step 3: Implement** UI; on 403 show error message from API

**Step 4: GREEN**

```powershell
cd frontend; npm test
```

**Step 5: Commit**

```powershell
git commit -m "feat(ee): admin UI and client filtering for allowed agents"
```

---

### Task 11: Full verification

**Step 1:**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
cd agent-runtime/session-agent; npm test
cd frontend; npm test
```

**Step 2:** Fix any regressions

**Step 3:** Grep for forbidden brand strings; confirm absent

**Step 4:** Commit any fixes; leave branch ready for PR

```powershell
git commit -m "test: finish OpenClaw and allowed-agents verification"
```

---

## Execution notes

- Prefer implementing Tasks 1–7 (CE OpenClaw) before 8–10 (EE), so CE stays shippable alone.
- If OpenClaw auth file discovery changes the placeholder shape, update Task 3 artifacts in the same change as Task 5 discovery — do not leave validators on the placeholder after merge.
- Keep Autonomous/Scheduled on `--local` only for v1 (no in-pod Gateway daemon requirement).
