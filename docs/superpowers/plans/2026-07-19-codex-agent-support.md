# Codex Agent Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Claude and Codex as selectable agents for Interactive, Autonomous, and Scheduled sessions, with per-session subscription/API-key authentication, persistent Codex login refresh, and configurable command/tool policies.

**Architecture:** Split the current agent image into Claude and Codex images that share a provider-neutral Node transport package. Persist agent/auth choices in PostgreSQL, construct provider-specific pod specs in a pure factory, and translate AgentHub MCP/policy input inside the selected runtime. Keep subscription credentials in provider-specific user Secrets and mount only the selected billing credential.

**Tech Stack:** .NET 10 / ASP.NET Core, KubernetesClient, PostgreSQL/Npgsql, Node.js 22, node-pty, Bash, Vue 3/Vite/Vitest, Docker, Helm 3, Docker Desktop Kubernetes.

## Global Constraints

- Existing session rows migrate to `AgentKind.Claude` and internal `AgentAuthMode.Auto`; new sessions accept only `Subscription` or `ApiKey`.
- A pod receives only the credential selected by `Agent` plus `AuthMode`; subscription mode never injects a provider API key and API-key mode never mounts a subscription Secret.
- Never log, return, fixture, or commit real provider credentials, callback tokens, or presigned URLs.
- Preserve existing Claude sessions, state keys, MCP configuration, sharing, Slack approvals, custom images, and resume behavior.
- Codex subscription state uses file storage at `$CODEX_HOME/auth.json`; headless interactive login uses device auth.
- Autonomous and Scheduled policy is default-deny for unmatched commands/tools; Kubernetes isolation remains the security boundary.
- Do not add any project association prohibited by the repository `AGENTS.md`.
- Every production behavior follows red-green-refactor and every task ends with independently passing focused tests.

---

## File map

- `backend/Models/SessionModels.cs`: public agent/auth/policy API models and duplication behavior.
- `backend/Persistence/PostgresSessionStore.cs`: idempotent schema migration and record mapping.
- `backend/Services/ProviderCredentialValidator.cs`: bounded provider auth JSON validation.
- `backend/Services/AgentPodSpecFactory.cs`: pure provider/auth-specific Kubernetes object construction.
- `backend/Services/KubernetesSessionService.cs`: orchestration and Secret I/O, delegating pod construction.
- `backend/Services/ISessionService.cs`: provider credential persistence contract.
- `backend/Controllers/InternalController.cs`: authenticated provider credential callback.
- `agent-runtime/common/`: shared PTY/WebSocket/persistence transport.
- `agent-runtime/claude/`: Claude Dockerfile, entrypoint, driver, and hooks.
- `agent-runtime/codex/`: Codex Dockerfile, entrypoint, driver, auth watcher, MCP converter, and policy hook.
- `frontend/src/components/NewSessionDialog.vue`: agent/auth/policy selection.
- `frontend/src/components/EditSessionDialog.vue`: next-start agent/auth/policy editing.
- `frontend/src/components/CredentialsDialog.vue`: OpenAI API-key write/clear status.
- `helm/open-agenthub/`: separate runtime images and compatibility defaults.
- `setup-dev.ps1`, `setup-dev.sh`: four-image local build and Docker Desktop acceptance support.

---

### Task 1: Persist agent, authentication mode, and structured policy

**Files:**
- Modify: `backend/Models/SessionModels.cs`
- Modify: `backend/Persistence/PostgresSessionStore.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`
- Modify: `tests/AgentHub.Api.Tests/SessionDuplicationTests.cs`
- Create: `tests/AgentHub.Api.Tests/SessionAgentModelTests.cs`

**Interfaces:**
- Produces: `AgentKind { Claude, Codex }`
- Produces: `AgentAuthMode { Auto, Subscription, ApiKey }`
- Produces: `AgentPolicy(IReadOnlyList<string> AllowedTools, IReadOnlyList<string> AllowedMcpTools, IReadOnlyList<string> AllowedCommands)`
- Produces: `CreateSessionRequest.Agent`, `.AuthMode`, `.Policy`; matching `SessionRecord` and `SessionInfo` properties.

- [ ] **Step 1: Write failing model and duplication tests**

```csharp
[Fact]
public void NewRequest_DefaultsToClaudeSubscription()
{
    var request = new CreateSessionRequest();
    Assert.Equal(AgentKind.Claude, request.Agent);
    Assert.Equal(AgentAuthMode.Subscription, request.AuthMode);
}

[Fact]
public void DuplicateRequest_CopiesAgentAuthAndPolicy()
{
    var source = new SessionRecord {
        Id = "s", Owner = "alice", Title = "Codex", Mode = SessionMode.Autonomous,
        Agent = AgentKind.Codex, AuthMode = AgentAuthMode.ApiKey,
        AgentSessionId = "thread", CallbackToken = "token", Status = "Succeeded",
        AgentPolicyJson = "{\"allowedTools\":[\"Read\"],\"allowedMcpTools\":[],\"allowedCommands\":[\"git status\"]}"
    };
    var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));
    Assert.Equal(AgentKind.Codex, copy.Agent);
    Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
    Assert.Equal(["git status"], copy.Policy.AllowedCommands);
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "SessionAgentModelTests|SessionDuplicationTests"`

Expected: compile failures for missing `AgentKind`, `AgentAuthMode`, `AgentPolicy`, and `AgentSessionId`.

- [ ] **Step 3: Add the minimal public model**

```csharp
public enum AgentKind { Claude, Codex }
public enum AgentAuthMode { Auto, Subscription, ApiKey }

public sealed record AgentPolicy
{
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedMcpTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedCommands { get; init; } = Array.Empty<string>();
}
```

Add `Agent`, `AuthMode`, and `Policy` to create/update/info models. Keep `AllowedTools` as a deprecated compatibility input and convert it only when `Policy` is empty. Reject `Auto` in new HTTP create requests inside `CreateSessionAsync`; allow it only when loading migrated records.

- [ ] **Step 4: Add an idempotent PostgreSQL migration**

Extend the startup DDL with:

```sql
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS agent TEXT NOT NULL DEFAULT 'Claude';
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS auth_mode TEXT NOT NULL DEFAULT 'Auto';
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS agent_policy JSONB;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS agent_session_id TEXT;
UPDATE sessions SET agent_session_id = claude_session_id WHERE agent_session_id IS NULL;
ALTER TABLE sessions ALTER COLUMN agent_session_id SET NOT NULL;
```

Read/write enum names as strings. Retain `claude_session_id` for this release so rollback remains possible; stop using it in application mappings.

- [ ] **Step 5: Run focused and full backend tests and verify GREEN**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "SessionAgentModelTests|SessionDuplicationTests"`

Expected: all focused tests pass.

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`

Expected: 0 failures; PostgreSQL-dependent tests may remain skipped when their test connection is absent.

- [ ] **Step 6: Commit**

```bash
git add backend/Models/SessionModels.cs backend/Persistence/PostgresSessionStore.cs backend/Services/KubernetesSessionService.cs tests/AgentHub.Api.Tests/SessionAgentModelTests.cs tests/AgentHub.Api.Tests/SessionDuplicationTests.cs
git commit -m "feat: persist session agent and authentication mode"
```

---

### Task 2: Store OpenAI API keys and provider subscription credentials safely

**Files:**
- Modify: `backend/Models/SessionModels.cs`
- Modify: `backend/Services/ISessionService.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`
- Modify: `backend/Controllers/InternalController.cs`
- Create: `backend/Services/ProviderCredentialValidator.cs`
- Create: `tests/AgentHub.Api.Tests/ProviderCredentialValidatorTests.cs`
- Create: `tests/AgentHub.Api.Tests/CredentialSelectionTests.cs`

**Interfaces:**
- Consumes: `AgentKind`, `AgentAuthMode` from Task 1.
- Produces: `Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct)`.
- Produces: `ProviderCredentialValidator.Validate(AgentKind agent, string json) : bool`.
- Produces: `UserCredentials.OpenAiApiKey` and `CredentialStatus.OpenAiApiKey`.

- [ ] **Step 1: Write failing validator tests**

```csharp
[Theory]
[InlineData(AgentKind.Claude, "{\"claudeAiOauth\":{\"accessToken\":\"x\"}}", true)]
[InlineData(AgentKind.Codex, "{\"tokens\":{\"access_token\":\"x\"}}", true)]
[InlineData(AgentKind.Codex, "{}", false)]
[InlineData(AgentKind.Codex, "not-json", false)]
public void Validate_RequiresProviderShape(AgentKind agent, string json, bool expected)
    => Assert.Equal(expected, ProviderCredentialValidator.Validate(agent, json));
```

Also test the 64 KiB limit and that values are never included in thrown/loggable messages.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter ProviderCredentialValidatorTests`

Expected: compile failure because the validator does not exist.

- [ ] **Step 3: Implement bounded structural validation**

```csharp
public static class ProviderCredentialValidator
{
    public const int MaxBytes = 64 * 1024;
    public static bool Validate(AgentKind agent, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes) return false;
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return agent switch {
            {
                AgentKind.Claude => root.TryGetProperty("claudeAiOauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object,
                AgentKind.Codex => root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object,
                _ => false
            };
        } catch (JsonException) { return false; }
    }
}
```

Use only structural markers proven by current CLI fixtures; do not parse or log token values.

- [ ] **Step 4: Write failing service/controller tests**

Assert that:

```csharp
Assert.Equal("openai_api_key", KubernetesSessionService.CredentialKey(nameof(UserCredentials.OpenAiApiKey)));
Assert.Equal("claude-u-...", KubernetesSessionService.ProviderSecretName("alice", AgentKind.Claude));
Assert.Equal("codex-u-...", KubernetesSessionService.ProviderSecretName("alice", AgentKind.Codex));
```

Add an InternalController test with a fake `ISessionService`: `/codex-credentials` accepts only an authenticated Codex Subscription session; provider mismatch and API-key sessions return `409 Conflict`; invalid JSON returns `400` without invoking storage.

- [ ] **Step 5: Implement Secret merge/status and generic callback**

Add `openai_api_key` to the existing merge/clear/status dictionaries. Replace `StoreClaudeCredentialsAsync` with `StoreProviderCredentialsAsync`, using `credentials.json` for Claude and `auth.json` for Codex. Expose one callback:

```csharp
[HttpPut("{agent}-credentials")]
public async Task<IActionResult> ProviderCredentials(string id, string agent, CancellationToken ct)
```

Parse `agent`, authenticate the callback token, require `rec.Agent == parsedAgent` and `rec.AuthMode == Subscription`, validate the body, then store under `rec.Owner`.

- [ ] **Step 6: Verify GREEN and commit**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "ProviderCredential|CredentialSelection"`

Expected: all focused tests pass.

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`

Expected: 0 failures.

```bash
git add backend tests/AgentHub.Api.Tests
git commit -m "feat: store Codex credentials and OpenAI API keys"
```

---

### Task 3: Build provider- and auth-specific Pod/CronJob specs

**Files:**
- Create: `backend/Services/AgentPodSpecFactory.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`
- Modify: `backend/appsettings.json`
- Create: `tests/AgentHub.Api.Tests/AgentPodSpecFactoryTests.cs`

**Interfaces:**
- Produces: `AgentRuntimeImages(string ClaudeImage, string CodexImage, string PullPolicy)`.
- Produces: `AgentPodSpecFactory.Build(SessionRecord record, CreateSessionRequest request, PodBuildContext context) : V1PodSpec`.
- `PodBuildContext` carries callback URLs, S3 URLs, Secret existence flags, runtime settings, and no secret values.

- [ ] **Step 1: Write the provider/auth matrix as failing theory tests**

```csharp
[Theory]
[InlineData(AgentKind.Claude, AgentAuthMode.Subscription, "runtime-claude", "claude", null)]
[InlineData(AgentKind.Claude, AgentAuthMode.ApiKey, "runtime-claude", null, "ANTHROPIC_API_KEY")]
[InlineData(AgentKind.Codex, AgentAuthMode.Subscription, "runtime-codex", "codex", null)]
[InlineData(AgentKind.Codex, AgentAuthMode.ApiKey, "runtime-codex", null, "CODEX_API_KEY")]
public void Build_MountsOnlySelectedCredential(AgentKind agent, AgentAuthMode auth,
    string expectedImage, string? expectedVolume, string? expectedEnv)
{
    var pod = Build(agent, auth);
    Assert.Equal(expectedImage, pod.Containers.Single().Image);
    Assert.Equal(expectedVolume is not null, pod.Volumes.Any(v => v.Name == expectedVolume));
    Assert.Equal(expectedEnv is not null, pod.Containers.Single().Env.Any(e => e.Name == expectedEnv));
}
```

Add assertions that the other provider Secret/API env is absent, custom-image copy init uses the selected runtime image, and CronJob templates reuse the exact same pod factory.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter AgentPodSpecFactoryTests`

Expected: compile failure for missing factory types.

- [ ] **Step 3: Extract the pure factory without changing Claude output**

Move the existing `BuildPodSpec` construction into `AgentPodSpecFactory`. First make a characterization test compare relevant fields for a legacy Claude/Auto request: image, entrypoint, env, volumes, mounts, security context, init containers, resources, and readiness probe.

Provider selection must use:

```csharp
var runtimeImage = record.Agent == AgentKind.Codex ? images.CodexImage : images.ClaudeImage;
```

Credential selection must use a `switch ((record.Agent, record.AuthMode))` and never add both branches. `Auto` is legal only for Claude and reproduces the old pair of optional Claude credential mount plus Anthropic key env.

- [ ] **Step 4: Add missing-credential preflight**

Before creating Autonomous/Scheduled resources, query only Secret key existence and return a failed session with this exact diagnostic shape:

```text
[agent] Cannot start Codex Autonomous session: Subscription credential is not stored.
```

Interactive Subscription bypasses preflight so login remains possible.

- [ ] **Step 5: Verify matrix and orchestration GREEN**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "AgentPodSpecFactory|CredentialSelection"`

Expected: all matrix cases pass and no unselected mount/env appears.

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj`

Expected: 0 failures.

- [ ] **Step 6: Commit**

```bash
git add backend/Services/AgentPodSpecFactory.cs backend/Services/KubernetesSessionService.cs backend/appsettings.json tests/AgentHub.Api.Tests/AgentPodSpecFactoryTests.cs
git commit -m "feat: select runtime and credentials per session"
```

---

### Task 4: Extract the shared transport and preserve Claude behavior

**Files:**
- Create: `agent-runtime/common/server.js`
- Create: `agent-runtime/common/driver-contract.js`
- Create: `agent-runtime/common/entrypoint-common.sh`
- Create: `agent-runtime/claude/driver.js`
- Create: `agent-runtime/claude/entrypoint.sh`
- Create: `agent-runtime/claude/Dockerfile`
- Move: existing Claude hooks into `agent-runtime/claude/hooks/`
- Modify: `agent-runtime/session-agent/package.json`
- Create: `agent-runtime/session-agent/test/claude-driver.test.js`
- Create: `agent-runtime/session-agent/test/common-server.test.js`

**Interfaces:**
- Driver exports `name`, `stateDir`, `buildCommand(env, allowResume)`, `isMissingResume(output, exitCode, elapsedMs)`, and `prepare(env)`.
- Common server consumes a driver selected by `AGENTHUB_DRIVER` and owns no provider flags.

- [ ] **Step 1: Write characterization tests before moving code**

```js
test('Claude interactive fresh command retains fixed session id', () => {
  assert.deepEqual(driver.buildCommand(env({ mode: 'interactive', agentSessionId: 'abc' }), true),
    { cmd: 'claude', args: ['--session-id', 'abc'] });
});

test('Claude autonomous command retains prompt and allowlist', () => {
  assert.deepEqual(driver.buildCommand(env({ mode: 'autonomous', prompt: 'fix it', allowedTools: ['Read'] }), true),
    { cmd: 'claude', args: ['-p', 'fix it', '--permission-mode', 'acceptEdits', '--allowedTools', 'Read'] });
});
```

Also characterize resume fallback, state archive contents excluding `.credentials.json`, scrollback cap, WebSocket replay, status posting, and shell path.

- [ ] **Step 2: Verify the new tests fail because modules do not exist**

Run: `npm test -- --test-name-pattern="Claude|common transport"`

Working directory: `agent-runtime/session-agent`.

Expected: module-not-found failures.

- [ ] **Step 3: Extract driver and common server**

`driver-contract.js` validates the required exports at startup:

```js
function loadDriver(path) {
  const driver = require(path);
  for (const key of ['name', 'stateDir', 'buildCommand', 'isMissingResume', 'prepare'])
    if (!driver[key]) throw new Error(`Agent driver missing ${key}`);
  return driver;
}
```

Move the existing transport code without behavioral cleanup until characterization tests pass. The common state tar command receives `driver.stateDir` and excludes the provider auth filename supplied by the driver.

- [ ] **Step 4: Build the Claude image and verify GREEN**

Run: `npm test`

Expected: existing 10 tests plus new characterization tests pass.

Run: `docker build -f agent-runtime/claude/Dockerfile -t open-agenthub-dev/agent-runtime-claude:test agent-runtime`

Expected: image builds and `docker run --rm --entrypoint claude ... --version` exits 0.

- [ ] **Step 5: Commit**

```bash
git add agent-runtime
git commit -m "refactor: split shared transport from Claude runtime"
```

---

### Task 5: Implement Codex runtime, auth persistence, MCP conversion, and resume

**Files:**
- Create: `agent-runtime/codex/Dockerfile`
- Create: `agent-runtime/codex/entrypoint.sh`
- Create: `agent-runtime/codex/driver.js`
- Create: `agent-runtime/codex/auth-watcher.js`
- Create: `agent-runtime/codex/mcp-config.js`
- Create: `agent-runtime/session-agent/test/codex-driver.test.js`
- Create: `agent-runtime/session-agent/test/codex-auth-watcher.test.js`
- Create: `agent-runtime/session-agent/test/codex-mcp-config.test.js`

**Interfaces:**
- Consumes the driver interface from Task 4.
- Produces `convertMcp(agentHubJson) : string` containing Codex TOML.
- Produces `watchCredential({ source, callbackUrl, callbackToken, fetchImpl, intervalMs })`.

- [ ] **Step 1: Write failing Codex command tests**

```js
test('Codex interactive starts TUI with workspace cwd', () => {
  assert.deepEqual(driver.buildCommand(env({ mode: 'interactive', resume: false }), true),
    { cmd: 'codex', args: [] });
});

test('Codex autonomous uses explicit sandbox and JSON events', () => {
  assert.deepEqual(driver.buildCommand(env({ mode: 'autonomous', prompt: 'fix it' }), true),
    { cmd: 'codex', args: ['exec', '--sandbox', 'workspace-write', '--json', '--dangerously-bypass-hook-trust', 'fix it'] });
});

test('Codex resume uses restored isolated state', () => {
  assert.deepEqual(driver.buildCommand(env({ mode: 'interactive', resume: true, stateRestored: true }), true),
    { cmd: 'codex', args: ['resume', '--last'] });
});
```

For non-interactive resume expect `codex exec resume --last <prompt>`.

- [ ] **Step 2: Write failing auth watcher tests**

Use a temporary directory and local HTTP server. Assert no initial upload, one upload after valid file creation, one after content change, retries after a 500, and no API-key-mode watcher. Assert captured logs never contain fixture token strings.

- [ ] **Step 3: Write failing MCP conversion tests**

```js
assert.match(convertMcp({ mcpServers: { docs: { command: 'npx', args: ['-y', 'server'] } } }),
  /\[mcp_servers\.docs\][\s\S]*command = "npx"/);
assert.throws(() => convertMcp({ mcpServers: { old: { type: 'sse', url: 'https://x' } } }),
  /unsupported transport/i);
```

Add fixtures for HTTP URL, bearer-token env var, env-backed headers, enabled tools, disabled tools, and rejection of literal Authorization secrets.

- [ ] **Step 4: Verify RED**

Run: `npm test -- --test-name-pattern="Codex"`

Expected: module-not-found failures.

- [ ] **Step 5: Implement minimal driver and entrypoint**

The entrypoint must:

```bash
export CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"
mkdir -p "$CODEX_HOME"
printf '%s\n' 'cli_auth_credentials_store = "file"' > "$CODEX_HOME/config.toml"
```

In Subscription mode, copy `/secrets/codex/auth.json` to `$CODEX_HOME/auth.json`, mode `600`, then start the watcher. With no file in Interactive mode, run `codex login --device-auth` before the TUI. In API-key Interactive mode, pipe `CODEX_API_KEY` to `codex login --with-api-key`, unset it, and do not start the watcher. In API-key Autonomous/Scheduled mode, expose `CODEX_API_KEY` on the `codex exec` process environment and its descendants, but not the long-lived session-agent parent or separate shell PTYs.

- [ ] **Step 6: Implement MCP TOML conversion and state exclusions**

Render deterministic TOML with sorted server names and JSON-style escaping. Merge generated MCP tables into the runtime-owned config without loading untrusted user auth redirection. Codex state tar excludes `auth.json` but includes sessions and configuration needed to resume.

- [ ] **Step 7: Verify GREEN and image contents**

Run: `npm test`

Expected: all common, Claude, Codex, watcher, and converter tests pass.

Run: `docker build -f agent-runtime/codex/Dockerfile -t open-agenthub-dev/agent-runtime-codex:test agent-runtime`

Expected: image builds.

Run: `docker run --rm --entrypoint codex open-agenthub-dev/agent-runtime-codex:test --version`

Expected: exits 0 and prints a Codex CLI version.

- [ ] **Step 8: Commit**

```bash
git add agent-runtime/codex agent-runtime/session-agent/test
git commit -m "feat: add Codex session runtime"
```

---

### Task 6: Enforce Codex command/tool policies and adapt collaboration hooks

**Files:**
- Create: `agent-runtime/codex/policy-hook.js`
- Create: `agent-runtime/codex/hooks.json`
- Modify: `agent-runtime/codex/entrypoint.sh`
- Modify: `backend/Controllers/InternalController.cs`
- Create: `backend/Services/AgentPolicyMatcher.cs`
- Create: `tests/AgentHub.Api.Tests/AgentPolicyMatcherTests.cs`
- Create: `agent-runtime/session-agent/test/codex-policy-hook.test.js`

**Interfaces:**
- Produces: `AgentPolicyMatcher.Decide(AgentPolicy policy, string tool, JsonElement input) : PolicyDecision`.
- Hook posts `{ tool, input }` to `/internal/sessions/{id}/agent-policy` and emits Codex `PreToolUse` deny/allow output.

- [ ] **Step 1: Write failing pure policy tests**

```csharp
[Theory]
[InlineData("Bash", "{\"command\":\"git status\"}", true)]
[InlineData("Bash", "{\"command\":\"git push\"}", false)]
[InlineData("mcp__docs__search", "{}", true)]
[InlineData("mcp__admin__delete", "{}", false)]
public void Decide_IsDefaultDeny(string tool, string input, bool allowed) { /* policy fixture */ }
```

Add cases for `git status && rm -rf /`, redirection, substitution, variable assignment, globs, and exact built-in patterns. The compound command must be denied if any component is unmatched.

- [ ] **Step 2: Verify RED, then implement matcher**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter AgentPolicyMatcherTests`

Expected: compile failure, then pass after implementing literal/prefix matching and conservative shell parsing.

- [ ] **Step 3: Write failing hook integration tests**

Spawn `policy-hook.js` with Codex hook JSON on stdin and a local HTTP server. Assert:

- allowed response exits 0 with no block;
- deny response emits the supported `PreToolUse` deny shape;
- callback failure fails closed in Autonomous/Scheduled;
- callback failure leaves Interactive permission flow available;
- the session MCP-sharing denial is checked before ordinary approval;
- no callback token is printed.

- [ ] **Step 4: Implement internal policy endpoint and Codex hook config**

The endpoint authenticates callback token, loads `rec.AgentPolicyJson`, applies `AgentPolicyMatcher`, and combines it with the existing live sharing MCP restriction. Runtime-owned `hooks.json` matches `Bash`, `apply_patch|Edit|Write`, and `mcp__.*`.

- [ ] **Step 5: Verify all policy and sharing tests GREEN**

Run: `dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "AgentPolicyMatcher|McpPolicy|Permission"`

Run: `npm test`

Expected: 0 failures.

- [ ] **Step 6: Commit**

```bash
git add backend agent-runtime/codex agent-runtime/session-agent/test tests/AgentHub.Api.Tests
git commit -m "feat: enforce Codex session policies"
```

---

### Task 7: Add agent/auth/policy selection and OpenAI credentials to the UI

**Files:**
- Modify: `frontend/src/components/NewSessionDialog.vue`
- Modify: `frontend/src/components/EditSessionDialog.vue`
- Modify: `frontend/src/components/DuplicateSessionDialog.vue`
- Modify: `frontend/src/components/CredentialsDialog.vue`
- Modify: `frontend/src/components/SessionList.vue`
- Modify: `frontend/src/components/SessionsView.vue`
- Modify: `frontend/src/components/TerminalView.vue`
- Modify: `frontend/src/lib/text.js`
- Create: `frontend/src/lib/agent.js`
- Create: `frontend/src/lib/agent.test.js`
- Modify: `frontend/src/components/views.test.js`

**Interfaces:**
- Produces: `agentOptions`, `authOptions(agent, legacyMode)`, `defaultPolicy(agent)`, `policyPayload(form)`.
- API payload sends `agent`, `authMode`, and structured `policy`.

- [ ] **Step 1: Write failing helper and component tests**

```js
it('defaults new sessions to Claude subscription', () => {
  expect(defaultAgentForm()).toMatchObject({ agent: 'Claude', authMode: 'Subscription' })
})

it('uses Codex command examples', () => {
  expect(defaultPolicy('Codex').allowedCommands).toContain('git status')
})
```

Mount `NewSessionDialog`, choose Codex + API key, submit, and assert the exact request:

```js
expect(api.createSession).toHaveBeenCalledWith(expect.objectContaining({
  agent: 'Codex', authMode: 'ApiKey',
  policy: { allowedTools: ['Read', 'Edit'], allowedMcpTools: [], allowedCommands: ['git status'] }
}))
```

Add tests that `Auto` appears only for an existing migrated session and OpenAI API-key clear sends `clear: ['openAiApiKey']`.

- [ ] **Step 2: Verify RED**

Run: `npm test -- --run src/lib/agent.test.js src/components/views.test.js`

Expected: missing helper/selectors and payload assertions fail.

- [ ] **Step 3: Implement selectors and conditional policy editor**

Place Agent and Billing directly below Mode. For Autonomous/Scheduled show three policy fields: allowed built-ins, allowed MCP tools, and allowed command prefixes (one per line). Interactive hides default-deny automation policy but retains stored values during edit.

Add a provider credential readiness hint using boolean credential status only. It warns but does not block Interactive subscription creation.

- [ ] **Step 4: Add list/detail/search labels and credential field**

Add `OpenAI API key` with password input, stored chip, clear semantics, and no value readback. Include `agent` and `authMode` in `sessionMatches` fields. Replace Claude-only copy with selected-agent wording.

- [ ] **Step 5: Verify frontend GREEN**

Run: `npm test -- --run`

Expected: all frontend tests pass.

Run: `npm run build`

Expected: Vite build exits 0 without Vue warnings.

- [ ] **Step 6: Commit**

```bash
git add frontend/src
git commit -m "feat: select agent authentication and policies in UI"
```

---

### Task 8: Wire separate images through Helm, CI, local setup, and documentation

**Files:**
- Modify: `helm/open-agenthub/values.yaml`
- Modify: `helm/open-agenthub/values-dev.yaml`
- Modify: `helm/open-agenthub/templates/_helpers.tpl`
- Modify: `helm/open-agenthub/templates/configmap.yaml`
- Modify: `k8s/20-backend.yaml`
- Modify: `setup-dev.ps1`
- Modify: `setup-dev.sh`
- Modify: `.github/workflows/build-images.yml`
- Modify: `.github/workflows/test.yml`
- Modify: `README.md`
- Create: `tests/helm/codex-runtime-values.ps1`

**Interfaces:**
- Helm values: `agent.images.claude` and `agent.images.codex`, with compatibility fallback from the existing registry/tag.
- Backend env: `AgentHub__ClaudeAgentImage`, `AgentHub__CodexAgentImage`.

- [ ] **Step 1: Write a failing rendered-chart assertion**

`tests/helm/codex-runtime-values.ps1` runs Helm template and asserts both lines:

```text
AgentHub__ClaudeAgentImage: "open-agenthub-dev/agent-runtime-claude:local"
AgentHub__CodexAgentImage: "open-agenthub-dev/agent-runtime-codex:local"
```

It also fails if either image value is empty.

- [ ] **Step 2: Verify RED**

Run: `pwsh -File tests/helm/codex-runtime-values.ps1`

Expected: missing values/helper assertion fails.

- [ ] **Step 3: Implement Helm and plain-manifest wiring**

Update helpers to render separate image names. Preserve existing `image.registry` and `image.tag`; default names become `agent-runtime-claude` and `agent-runtime-codex`. ConfigMap passes both to backend options.

- [ ] **Step 4: Update build workflows and local scripts**

CI matrix becomes:

```yaml
component: [backend, frontend, agent-runtime-claude, agent-runtime-codex]
```

Use explicit context/file mapping for the two runtime entries. Local scripts build:

```text
open-agenthub-dev/backend:local
open-agenthub-dev/frontend:local
open-agenthub-dev/agent-runtime-claude:local
open-agenthub-dev/agent-runtime-codex:local
```

Keep the docker-desktop context refusal unchanged.

- [ ] **Step 5: Update documentation**

Document agent/auth selection, Device Code login, Codex API-key behavior, separate images, selected-only mounts, custom image injection, state keys, policy guardrail limits, Kubernetes encryption at rest, and the four-image local build. Do not claim that Kubernetes Secrets are encrypted merely because they are Secrets.

- [ ] **Step 6: Verify chart, scripts, docs, and images**

Run: `helm lint helm/open-agenthub -f helm/open-agenthub/values-dev.yaml --set-string postgres.password=test-only`

Run: `pwsh -File tests/helm/codex-runtime-values.ps1`

Run: `git diff --check`

Expected: all exit 0.

- [ ] **Step 7: Commit**

```bash
git add helm k8s setup-dev.ps1 setup-dev.sh .github README.md tests/helm
git commit -m "build: publish and deploy separate agent runtimes"
```

---

### Task 9: Full regression verification and Docker Desktop Kubernetes acceptance

**Files:**
- Create: `docs/testing/codex-docker-desktop-acceptance.md`
- Modify only if a failing acceptance check exposes a defect: files owned by Tasks 1-8, with a new failing regression test first.

**Interfaces:**
- Produces a non-secret acceptance report containing commands, counts, resource names, image tags/digests, and outcomes.

- [ ] **Step 1: Run all local verification from a clean worktree**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
Push-Location frontend; npm ci; npm test -- --run; npm run build; Pop-Location
Push-Location agent-runtime/session-agent; npm ci; npm test; Pop-Location
helm lint helm/open-agenthub -f helm/open-agenthub/values-dev.yaml --set-string postgres.password=test-only
pwsh -File tests/helm/codex-runtime-values.ps1
git diff --check
```

Expected: every command exits 0. Record the exact test counts. Treat existing dependency-audit warnings separately from test failures; do not run breaking automatic audit fixes in this feature.

- [ ] **Step 2: Build and deploy on Docker Desktop Kubernetes**

First verify:

```powershell
kubectl config current-context
```

Expected: exactly `docker-desktop`. If not, stop without switching context.

Run: `.\setup-dev.ps1 -NoPortForward`

Expected: four images build, Helm upgrade succeeds, PostgreSQL/backend/frontend rollouts become ready, and health checks pass.

- [ ] **Step 3: Verify generated session resources without real credentials**

Through the local development API, create synthetic sessions for all provider/auth combinations. Use a synthetic credential fixture only through the internal test path. Inspect with:

```powershell
kubectl -n agenthub-dev-sessions get pods,cronjobs -o wide
kubectl -n agenthub-dev-sessions get pod <name> -o json
```

For every resource, record assertions that only the selected provider mount/env exists. Do not include Secret data in output or the report.

- [ ] **Step 4: Exercise behavior matrix**

Verify:

- unauthenticated Codex Interactive reaches device-code login;
- missing selected credential fails Autonomous/Scheduled with the specified diagnostic;
- synthetic subscription file creation and refresh update only the Codex owner Secret;
- API-key mode does not upload an auth file;
- allowed command executes and unmatched/compound unsafe command is denied;
- MCP stdio/HTTP conversion succeeds and unsupported transport fails before CLI start;
- Claude Interactive/Autonomous still start;
- terminal reconnect replays scrollback;
- pause/resume uses provider-specific state and falls back fresh at most once;
- Codex Scheduled creates a runnable CronJob with selected policy/auth.

Do not complete a real Codex subscription login. Leave the Device Code screen for the user if they want to perform that final account-bound check.

- [ ] **Step 5: Record acceptance evidence and fix only test-proven defects**

Write `docs/testing/codex-docker-desktop-acceptance.md` with environment versions, image tags, test counts, resource matrix, non-secret observations, and any account-bound step left for the user. For each defect, first add a focused failing automated test, then implement the minimal fix and rerun the relevant matrix row.

- [ ] **Step 6: Run final verification again**

Repeat Step 1 after all acceptance fixes. Then run:

```powershell
git status --short
git log --oneline --decorate -10
```

Expected: only the acceptance report is uncommitted; all tests/builds/lint pass.

- [ ] **Step 7: Commit**

```bash
git add docs/testing/codex-docker-desktop-acceptance.md
git commit -m "test: verify Codex support on Docker Desktop Kubernetes"
```

---

## Final self-review checklist

- [ ] Map every acceptance criterion in the design to at least one passing test or acceptance row.
- [ ] Search for `ClaudeSessionId`, `AgentImage`, Claude-only UI copy, and old runtime image names; retain only intentional compatibility references.
- [ ] Search tracked files for token-shaped fixtures and verify all are unmistakably synthetic.
- [ ] Inspect rendered Pods to confirm selected-only Secret mounts and env references.
- [ ] Confirm no real local Codex `auth.json` was read, copied, logged, or committed.
- [ ] Confirm the main checkout remains untouched and all commits are on `feat/codex-support`.
