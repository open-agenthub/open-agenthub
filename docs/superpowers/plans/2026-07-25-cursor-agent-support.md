# Cursor Agent Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Cursor as a first-class selectable agent peer to Claude and Codex for Interactive, Autonomous, and Scheduled sessions, with per-session Subscription or API-key authentication, file-store login persistence, MCP translation, and CLI permission policy via `cli-config.json`.

**Architecture:** Keep the existing provider-neutral Node PTY/WebSocket/persistence transport. Add a third runtime image (`agent-runtime-cursor`) with a Cursor-only driver that owns CLI install, auth restore/watch, resume, MCP→`~/.cursor/mcp.json`, and policy→`cli-config.json`. Extend backend enum validation, credential Secrets, pod factory, and S3 state keys so a session mounts only the selected Cursor credential and uses `cursor-state.tgz`. Frontend agent/auth selectors and Credentials gain Cursor without changing Claude/Codex behavior.

**Tech Stack:** .NET 10 / ASP.NET Core, KubernetesClient, PostgreSQL/Npgsql, Node.js 22, node-pty, Bash, Vue 3/Vite/Vitest, Docker, Helm 3, Docker Desktop Kubernetes.

## Global Constraints

- Never associate this project with any unrelated company brand in code, comments, commits, READMEs, configs, sample domains, emails, or docs. Author/contact remains Maik Boltze / `open-agenthub` / `open-agenthub@mail.on-mb.com` only. Follow repository `AGENTS.md`.
- Never log, return, fixture, or commit real Cursor API keys, subscription tokens, callback tokens, or presigned URLs. Tests use unmistakably synthetic fixtures only.
- Cursor is a peer to Claude and Codex via the same PTY + driver-image pattern: shared transport in `agent-runtime/common/`, provider-specific code only under `agent-runtime/cursor/`.
- A pod receives only the credential selected by `Agent` plus `AuthMode`; Cursor Subscription never injects `CURSOR_API_KEY`; Cursor API-key never mounts the Cursor subscription Secret.
- Cursor does not use Claude-only legacy `AgentAuthMode.Auto`.
- Autonomous and Scheduled policy is default-deny; Kubernetes isolation remains the security boundary. Do not document `cli-config.json` allowlists as a complete container sandbox.
- Exact Cursor file-store credential **filename and JSON shape are unknown until discovered** during the runtime Task by inspecting `AGENT_CLI_CREDENTIAL_STORE=file` output after a synthetic `agent login` (or documented CLI fixture). Plan uses a **placeholder shape** that must be replaced and pinned in validator tests before merge.
- Every production behavior follows red-green-refactor; every task ends with independently passing focused tests.
- On Windows workstations use PowerShell-friendly `dotnet test` / `npm test` commands; Docker, Helm, and kubectl commands remain as documented for image/chart/acceptance work.

### Cursor CLI facts (pin in driver + Dockerfile)

| Fact | Contract |
| --- | --- |
| Binary | `agent` and/or `cursor-agent` (image must expose a stable path; prefer `agent`) |
| API key env | `CURSOR_API_KEY` |
| Subscription login | `agent login` with `NO_OPEN_BROWSER=1` and `AGENT_CLI_CREDENTIAL_STORE=file` |
| Autonomous / Scheduled | `agent -p --force --trust ...` (exact flags owned by `driver.js`, pinned to Dockerfile CLI version) |
| Permissions | Written to `~/.cursor/cli-config.json` (or `CURSOR_CONFIG_DIR`) |
| MCP | Written to `~/.cursor/mcp.json` |
| State dir | Cursor home/config under `$HOME/.cursor` (exclude credential file from state tar) |

### Placeholder credential shape (replace after discovery)

Until the file store is inspected in Task 5:

- **Placeholder Secret key / filename:** `cursor-credentials.json`
- **Placeholder structural marker (synthetic only):**

```json
{"cursorAuth":{"accessToken":"synthetic-test-token-not-real"}}
```

Validator accepts Cursor JSON only when the root is an object and contains a top-level `cursorAuth` object. After discovery, rename the Secret key and rewrite this marker in:

- `ProviderCredentialValidator`
- `CredentialSecretFactory.CreateProviderSecret`
- `agent-runtime/cursor/auth-watcher.js` shape check
- focused backend/runtime tests

Do not invent additional fields beyond the discovered store.

---

## File map

- `backend/Models/SessionModels.cs`: `AgentKind.Cursor`, validation allowing Cursor + Subscription/ApiKey only.
- `backend/Services/ProviderCredentialValidator.cs`: Cursor subscription JSON structural validation (placeholder then pinned).
- `backend/Services/CredentialSecretFactory.cs`: `cursor_api_key`, Cursor provider Secret file key, status booleans.
- `backend/Services/KubernetesSessionService.cs`: Cursor Secret name, image option, credential status merge, pod context.
- `backend/Services/AgentPodSpecFactory.cs`: Cursor image, subscription volume `/secrets/cursor`, `CURSOR_API_KEY` env, no unselected creds.
- `backend/Storage/S3ArtifactStore.cs` (+ `IArtifactStore`): `cursor-state.tgz`.
- `backend/Controllers/InternalController.cs`: existing `{agent}-credentials` route must accept `cursor` for Cursor Subscription sessions.
- `backend/appsettings.json`: `CursorAgentImage` option.
- `agent-runtime/cursor/`: Dockerfile, entrypoint, driver, auth watcher, MCP converter, cli-config policy writer.
- `agent-runtime/session-agent/test/cursor-*.test.js`: runtime unit tests.
- `frontend/src/lib/agent.js` (+ tests): Cursor option, policy defaults, credential readiness.
- `frontend/src/components/NewSessionDialog.vue`, `EditSessionDialog.vue`, `DuplicateSessionDialog.vue`, `CredentialsDialog.vue`, list/detail/search views.
- `frontend/src/components/agent-dialogs.test.js`, `views.test.js`: Cursor UI coverage.
- `helm/open-agenthub/`: `agent.images.cursor`, ConfigMap `AgentHub__CursorAgentImage`.
- `k8s/20-backend.yaml`, `setup-dev.ps1`, `setup-dev.sh`, `.github/workflows/build-images.yml`, `.github/workflows/test.yml`.
- `README.md`, release notes / deploy docs, `docs/testing/cursor-docker-desktop-acceptance.md`.

---

### Task 1: Accept `AgentKind.Cursor` in models and validation

**Files:**
- Modify: `backend/Models/SessionModels.cs`
- Modify: `tests/AgentHub.Api.Tests/SessionAgentModelTests.cs`
- Modify: `tests/AgentHub.Api.Tests/SessionDuplicationTests.cs` (add Cursor copy case)

**Interfaces:**
- Extends: `AgentKind { Claude, Codex, Cursor }`
- `AgentConfiguration.ValidateAgent` must accept `Cursor`; reject `Auto` for Cursor create/update/duplicate.

- [ ] **Step 1: Write failing validation and duplication tests**

```csharp
[Fact]
public void CreateConfiguration_AcceptsCursorSubscriptionAndApiKey()
{
    AgentConfiguration.ValidateForCreate(AgentKind.Cursor, AgentAuthMode.Subscription);
    AgentConfiguration.ValidateForCreate(AgentKind.Cursor, AgentAuthMode.ApiKey);
}

[Fact]
public void CreateConfiguration_RejectsCursorAuto()
{
    Assert.Throws<ArgumentException>(() =>
        AgentConfiguration.ValidateForCreate(AgentKind.Cursor, AgentAuthMode.Auto));
}

[Fact]
public void DuplicateRequest_CopiesCursorAgentAuthAndPolicy()
{
    var source = new SessionRecord {
        Id = "s", Owner = "alice", Title = "Cursor", Mode = SessionMode.Autonomous,
        Agent = AgentKind.Cursor, AuthMode = AgentAuthMode.ApiKey,
        AgentSessionId = "chat", CallbackToken = "token", Status = "Succeeded",
        AgentPolicyJson = "{\"allowedTools\":[\"Shell(git status)\"],\"allowedMcpTools\":[],\"allowedCommands\":[]}"
    };
    var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));
    Assert.Equal(AgentKind.Cursor, copy.Agent);
    Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
    Assert.Equal(["Shell(git status)"], copy.Policy.AllowedTools);
}
```

Also extend `CreateConfiguration_RejectsUnknownAgentKinds` so previously invalid enums still fail, and add a positive case that `(AgentKind)2` or the new `Cursor` value is accepted once defined (adjust ordinal assertions carefully).

- [ ] **Step 2: Run focused tests and verify RED**

Run (PowerShell):

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "SessionAgentModelTests|SessionDuplicationTests" --nologo
```

Expected: compile or assertion failures because `AgentKind.Cursor` is missing or rejected by `ValidateAgent`.

- [ ] **Step 3: Minimal implementation**

```csharp
public enum AgentKind { Claude, Codex, Cursor }

private static void ValidateAgent(AgentKind agent)
{
    if (agent is not AgentKind.Claude and not AgentKind.Codex and not AgentKind.Cursor)
        throw new ArgumentException("Unsupported agent kind.");
}
```

Ensure `ValidateAuthMode` still rejects `Auto` for all public creates (Cursor never gets an Auto exception path). Leave PostgreSQL schema unchanged (agent is already `TEXT`).

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "SessionAgentModelTests|SessionDuplicationTests" --nologo
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
```

Expected: focused and full backend suites pass (Postgres-dependent tests may skip).

- [ ] **Step 5: Commit**

```bash
git add backend/Models/SessionModels.cs tests/AgentHub.Api.Tests/SessionAgentModelTests.cs tests/AgentHub.Api.Tests/SessionDuplicationTests.cs
git commit -m "feat: accept Cursor as a session agent kind"
```

---

### Task 2: Cursor API key, subscription Secret, and placeholder validator

**Files:**
- Modify: `backend/Models/SessionModels.cs` (`UserCredentials.CursorApiKey`, `CredentialStatus.CursorApiKey`, `CredentialStatus.CursorSubscription`)
- Modify: `backend/Services/ProviderCredentialValidator.cs`
- Modify: `backend/Services/CredentialSecretFactory.cs`
- Modify: `backend/Services/KubernetesSessionService.cs` (`ProviderSecretName`, credential status read)
- Modify: `backend/Controllers/InternalController.cs` (route already generic; extend parse/`AgentKind` mapping tests)
- Modify: `tests/AgentHub.Api.Tests/ProviderCredentialValidatorTests.cs`
- Modify: `tests/AgentHub.Api.Tests/CredentialSelectionTests.cs`
- Modify: `tests/AgentHub.Api.Tests/CredentialSecretFactoryTests.cs`
- Modify: `tests/AgentHub.Api.Tests/CredentialInputLimitTests.cs` (Cursor route coverage optional if shared)

**Interfaces:**
- Produces: Secret key `cursor_api_key` for general credentials.
- Produces: provider Secret name `cursor-u-{hash}` with file key **placeholder** `cursor-credentials.json`.
- Produces: `ProviderCredentialValidator` Cursor branch using placeholder `cursorAuth` object marker.
- Internal PUT `/internal/sessions/{id}/cursor-credentials` stores only for `AgentKind.Cursor` + `Subscription`.

- [ ] **Step 1: Write failing validator tests (placeholder shape)**

```csharp
[Theory]
[InlineData(AgentKind.Cursor, "{\"cursorAuth\":{\"accessToken\":\"x\"}}", true)]
[InlineData(AgentKind.Cursor, "{\"tokens\":{}}", false)]
[InlineData(AgentKind.Cursor, "{}", false)]
[InlineData(AgentKind.Cursor, "not-json", false)]
[InlineData(AgentKind.Claude, "{\"cursorAuth\":{}}", false)]
public void Validate_RequiresProviderShape(AgentKind agent, string json, bool expected)
    => Assert.Equal(expected, ProviderCredentialValidator.Validate(agent, json));
```

Add a comment in the test file: `// PLACEHOLDER: replace cursorAuth marker after file-store discovery in Task 5.`

Also assert the 64 KiB limit and that thrown/logged paths never include fixture token strings.

- [ ] **Step 2: Verify RED**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter ProviderCredentialValidatorTests --nologo
```

Expected: Cursor cases fail (always `false` via `_ => false`).

- [ ] **Step 3: Implement placeholder validation + Secret wiring**

```csharp
AgentKind.Cursor => root.TryGetProperty("cursorAuth", out var auth) && auth.ValueKind == JsonValueKind.Object,
```

In `CredentialSecretFactory`:

```csharp
["cursorApiKey"] = "cursor_api_key",
// ...
CursorApiKey = data.ContainsKey("cursor_api_key"),
CursorSubscription = cursorSubscription?.ContainsKey("cursor-credentials.json") == true
```

```csharp
AgentKind.Cursor => "cursor-credentials.json", // PLACEHOLDER filename
```

```csharp
AgentKind.Cursor => $"cursor-{Sanitize(owner)}",
```

Extend `GetCredentialStatus` to load the Cursor provider Secret alongside Claude/Codex. Map route agent string `"cursor"` → `AgentKind.Cursor` where the controller parses the `{agent}` segment.

- [ ] **Step 4: Write failing InternalController / naming tests**

```csharp
Assert.Equal("cursor_api_key", KubernetesSessionService.CredentialKey(nameof(UserCredentials.CursorApiKey)));
Assert.Equal("cursor-u-2bd806c97f0e00af", KubernetesSessionService.ProviderSecretName("alice", AgentKind.Cursor));
```

Assert `/cursor-credentials` accepts authenticated Cursor Subscription; Claude/Codex mismatch and Cursor ApiKey sessions return `409`; invalid JSON returns `400` without storage.

- [ ] **Step 5: Verify GREEN and commit**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "ProviderCredential|CredentialSelection|CredentialSecret" --nologo
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
```

```bash
git add backend tests/AgentHub.Api.Tests
git commit -m "feat: store Cursor API keys and subscription credentials"
```

---

### Task 3: Pod/CronJob factory selects Cursor image, mounts, and env

**Files:**
- Modify: `backend/Services/AgentPodSpecFactory.cs`
- Modify: `backend/Services/KubernetesSessionService.cs` (options + `PodBuildContext`)
- Modify: `backend/appsettings.json`
- Modify: `tests/AgentHub.Api.Tests/AgentPodSpecFactoryTests.cs`

**Interfaces:**
- Extends: `AgentRuntimeImages(..., string CursorImage, ...)`.
- Extends: `PodBuildContext.CursorCredentialSecretName`.
- Extends: `AgentHubOptions.CursorAgentImage`.

- [ ] **Step 1: Write failing matrix theory tests**

```csharp
[Theory]
[InlineData(AgentKind.Cursor, AgentAuthMode.Subscription, "runtime-cursor", "cursor", null)]
[InlineData(AgentKind.Cursor, AgentAuthMode.ApiKey, "runtime-cursor", null, "CURSOR_API_KEY")]
public void Build_MountsOnlySelectedCredential(AgentKind agent, AgentAuthMode auth,
    string expectedImage, string? expectedVolume, string? expectedEnv)
{
    var pod = Build(agent, auth);
    Assert.Equal(expectedImage, pod.Containers.Single().Image);
    Assert.Equal(expectedVolume is not null, pod.Volumes.Any(v => v.Name == expectedVolume));
    Assert.Equal(expectedEnv is not null, pod.Containers.Single().Env.Any(e => e.Name == expectedEnv));
    Assert.DoesNotContain(pod.Volumes, v => v.Name is "claude" or "codex");
    Assert.DoesNotContain(pod.Containers.Single().Env, e => e.Name is "ANTHROPIC_API_KEY" or "CODEX_API_KEY");
}
```

Also assert:

- Claude/Codex matrix rows remain unchanged.
- Custom-image init copies from the Cursor runtime image when `record.Agent == Cursor`.
- CronJob templates reuse the same factory.
- Missing Credential diagnostic: `[agent] Cannot start Cursor Autonomous session: Subscription credential is not stored.`

- [ ] **Step 2: Verify RED**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter AgentPodSpecFactoryTests --nologo
```

Expected: compile/assert failures for missing Cursor image/volume/env branches.

- [ ] **Step 3: Minimal factory implementation**

```csharp
var runtimeImage = record.Agent switch
{
    AgentKind.Codex => images.CodexImage,
    AgentKind.Cursor => images.CursorImage,
    _ => images.ClaudeImage
};

case (AgentKind.Cursor, AgentAuthMode.Subscription):
    AddSubscriptionVolume("cursor", context.CursorCredentialSecretName);
    break;
case (AgentKind.Cursor, AgentAuthMode.ApiKey):
    AddApiKey("CURSOR_API_KEY", "cursor_api_key");
    break;
```

Wire `CursorAgentImage` through options and orchestrator context. Do not add Cursor-specific telemetry unless already shared.

- [ ] **Step 4: Verify GREEN**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "AgentPodSpecFactory|CredentialSelection" --nologo
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
```

- [ ] **Step 5: Commit**

```bash
git add backend/Services/AgentPodSpecFactory.cs backend/Services/KubernetesSessionService.cs backend/appsettings.json tests/AgentHub.Api.Tests/AgentPodSpecFactoryTests.cs
git commit -m "feat: select Cursor runtime image and credentials per session"
```

---

### Task 4: Persist Cursor session state as `cursor-state.tgz`

**Files:**
- Modify: `backend/Storage/S3ArtifactStore.cs` (`IArtifactStore.StateKey`)
- Modify: `tests/AgentHub.Api.Tests/ArtifactStoreKeyTests.cs`

**Interfaces:**
- `StateKey(..., AgentKind.Cursor)` → `sessions/{owner}/{id}/cursor-state.tgz`

- [ ] **Step 1: Write failing key tests**

```csharp
[Fact]
public void StateKey_UsesProviderSpecificArchiveName()
{
    Assert.Equal(
        "sessions/alice/session-id/cursor-state.tgz",
        IArtifactStore.StateKey("alice", "session-id", AgentKind.Cursor));
    Assert.NotEqual(
        IArtifactStore.StateKey("alice", "session-id", AgentKind.Codex),
        IArtifactStore.StateKey("alice", "session-id", AgentKind.Cursor));
}
```

- [ ] **Step 2: Verify RED, implement, GREEN**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter ArtifactStoreKeyTests --nologo
```

```csharp
AgentKind.Cursor => "cursor-state.tgz",
```

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter ArtifactStoreKeyTests --nologo
```

- [ ] **Step 3: Commit**

```bash
git add backend/Storage/S3ArtifactStore.cs tests/AgentHub.Api.Tests/ArtifactStoreKeyTests.cs
git commit -m "feat: use cursor-state.tgz for Cursor session archives"
```

---

### Task 5: Cursor runtime image — driver, auth watcher, MCP, cli-config policy

**Files:**
- Create: `agent-runtime/cursor/Dockerfile`
- Create: `agent-runtime/cursor/entrypoint.sh`
- Create: `agent-runtime/cursor/driver.js`
- Create: `agent-runtime/cursor/auth-watcher.js`
- Create: `agent-runtime/cursor/mcp-config.js`
- Create: `agent-runtime/cursor/cli-config.js` (policy → `cli-config.json`)
- Create: `agent-runtime/session-agent/test/cursor-driver.test.js`
- Create: `agent-runtime/session-agent/test/cursor-auth-watcher.test.js`
- Create: `agent-runtime/session-agent/test/cursor-mcp-config.test.js`
- Create: `agent-runtime/session-agent/test/cursor-cli-config.test.js`
- Optionally: `agent-runtime/session-agent/test/fixtures/cursor-container/` smoke scripts mirroring Codex

**Interfaces:**
- Driver exports: `name`, `stateDir`, `authFilename`, `buildCommand`, `isResumeCommand` (if needed), `isMissingResume`, `prepare`.
- `convertMcp(agentHubJson) →` Cursor `mcp.json` object/string at `~/.cursor/mcp.json`.
- `writeCliConfig(policy, mode) →` permissions for Autonomous/Scheduled (`approvalMode: allowlist`, allow tokens from policy, sandbox disabled for containers).
- `watchCredential({ source, callbackUrl, callbackToken, ... })` PUTs to `/cursor-credentials`.

**Discovery gate (do this before locking validator shape):**

1. In a throwaway container or local install of the pinned CLI version, set `AGENT_CLI_CREDENTIAL_STORE=file`, run a non-secret exploratory login path or read CLI docs/fixtures.
2. Record the exact credential filename under `~/.cursor` (or configured config dir) and the minimal JSON top-level keys.
3. Replace placeholder `cursor-credentials.json` / `cursorAuth` everywhere from Task 2 and update tests in the same commit series as this Task (or an immediate follow-up commit titled `fix: pin Cursor credential file shape`).

- [ ] **Step 1: Write failing driver command tests**

```js
test('Cursor interactive starts TUI', () => {
  assert.deepEqual(driver.buildCommand(env({ mode: 'interactive', resume: false }), true),
    { cmd: 'agent', args: [] });
});

test('Cursor autonomous uses print, force, and trust', () => {
  assert.deepEqual(driver.buildCommand(env({ mode: 'autonomous', prompt: 'fix it' }), true),
    { cmd: 'agent', args: ['-p', '--force', '--trust', 'fix it'] });
});

test('Cursor resume uses --resume when chat id present', () => {
  assert.deepEqual(
    driver.buildCommand(env({
      mode: 'interactive', resume: true, stateRestored: true, agentSessionId: 'chat-1'
    }), true),
    { cmd: 'agent', args: ['--resume', 'chat-1'] });
});
```

Adjust exact args to the pinned CLI help once verified; keep tests as the source of truth. For MCP-configured autonomous runs, expect `--approve-mcps` when `AGENTHUB_HAS_MCP=1`.

- [ ] **Step 2: Write failing auth watcher tests**

Use a temp directory + local HTTP server. Assert:

- no initial upload when baseline matches restored secret;
- one upload after valid file create/change;
- retries after HTTP 500;
- no watcher activity in API-key mode;
- logs never contain fixture token strings;
- upload URL ends with `/cursor-credentials`.

Shape check must use the **discovered** (or still-placeholder) JSON marker consistently with the backend validator.

- [ ] **Step 3: Write failing MCP and cli-config tests**

```js
test('MCP converts stdio server into cursor mcp.json', () => {
  const out = convertMcp({ mcpServers: { docs: { command: 'npx', args: ['-y', 'server'] } } });
  const parsed = JSON.parse(out);
  assert.equal(parsed.mcpServers.docs.command, 'npx');
});

test('unsupported transport fails before write', () => {
  assert.throws(() => convertMcp({ mcpServers: { old: { type: 'sse', url: 'https://x' } } }),
    /unsupported transport/i);
});

test('cli-config maps allowedTools and MCP tools into permissions.allow', () => {
  const cfg = writeCliConfig({
    mode: 'autonomous',
    allowedTools: ['Shell(git status)', 'Read(**)'],
    allowedMcpTools: ['docs:search'],
    allowedCommands: []
  });
  const parsed = JSON.parse(cfg);
  assert.equal(parsed.permissions.approvalMode, 'allowlist');
  assert.ok(parsed.permissions.allow.includes('Shell(git status)'));
  assert.ok(parsed.permissions.allow.some(t => /Mcp\(docs:search\)/i.test(t) || t === 'Mcp(docs:search)'));
});
```

Empty policy sections remain default-deny for automation. Interactive may seed allow/deny without `--force`.

- [ ] **Step 4: Verify RED**

```powershell
Push-Location agent-runtime/session-agent
npm test -- --test-name-pattern="Cursor"
Pop-Location
```

Expected: module-not-found failures.

- [ ] **Step 5: Implement entrypoint + driver**

Entrypoint sketch:

```bash
export AGENTHUB_STATE_DIR=.cursor
export CURSOR_CONFIG_DIR="${CURSOR_CONFIG_DIR:-$HOME/.cursor}"
mkdir -p "$CURSOR_CONFIG_DIR"
chmod 700 "$CURSOR_CONFIG_DIR"
umask 077
export AGENT_CLI_CREDENTIAL_STORE=file
export NO_OPEN_BROWSER=1

# Policy before CLI start
node "$RUNTIME/cursor/cli-config.js" > "$CURSOR_CONFIG_DIR/cli-config.json"
chmod 600 "$CURSOR_CONFIG_DIR/cli-config.json"

if [ "${AGENTHUB_HAS_MCP:-0}" = "1" ] && [ -f /secrets/mcp/mcp.json ]; then
  node "$RUNTIME/cursor/mcp-config.js" /secrets/mcp/mcp.json > "$CURSOR_CONFIG_DIR/mcp.json"
  chmod 600 "$CURSOR_CONFIG_DIR/mcp.json"
fi

# State restore precedes auth; never trust archived credentials
rm -f "$CURSOR_CONFIG_DIR/<DISCOVERED_OR_PLACEHOLDER_CREDENTIAL_FILE>"

case "${AGENTHUB_AUTH_MODE:-}" in
apikey)
  : "${CURSOR_API_KEY:?CURSOR_API_KEY required}"
  # Interactive may run `agent login` with API key if CLI requires it; Autonomous keeps env for child via driver.prepare
  ;;
subscription)
  if [ -f /secrets/cursor/<credential-file> ]; then
    cp /secrets/cursor/<credential-file> "$CURSOR_CONFIG_DIR/<credential-file>"
    chmod 600 "$CURSOR_CONFIG_DIR/<credential-file>"
  fi
  # start auth-watcher.js when callback env present
  if [ ! -f "$CURSOR_CONFIG_DIR/<credential-file>" ] && [ "${AGENTHUB_MODE:-interactive}" = "interactive" ]; then
    agent login   # headless-friendly; file store already set
  fi
  ;;
*)
  echo "[entrypoint] ERROR: unsupported Cursor authentication mode." >&2
  exit 1
  ;;
esac

export AGENTHUB_DRIVER="$RUNTIME/cursor/driver.js"
exec node "$RUNTIME/common/server.js"
```

`prepare(env)` must strip `CURSOR_API_KEY` from the long-lived server env and return `childEnv` only for Autonomous/Scheduled API-key mode (mirror Codex). State tar excludes the credential filename via `driver.authFilename`.

- [ ] **Step 6: Dockerfile**

Mirror Codex layout: Node 22 bookworm builder + slim runtime; install pinned Cursor Agent CLI (document version ARG); copy `common/` + `cursor/`; ensure `agent` (and `cursor-agent` if distinct) are executable; `USER 1000:1000`; entrypoint script.

Do not copy Claude/Codex provider trees into the Cursor image.

- [ ] **Step 7: Verify GREEN and image**

```powershell
Push-Location agent-runtime/session-agent
npm test
Pop-Location
docker build -f agent-runtime/cursor/Dockerfile -t open-agenthub-dev/agent-runtime-cursor:test agent-runtime
docker run --rm --entrypoint agent open-agenthub-dev/agent-runtime-cursor:test --version
```

Expected: all tests pass; image builds; version command exits 0.

If discovery changed the credential shape, also re-run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "ProviderCredential|CredentialSecret|CredentialSelection" --nologo
```

- [ ] **Step 8: Commit**

```bash
git add agent-runtime/cursor agent-runtime/session-agent/test backend tests/AgentHub.Api.Tests
git commit -m "feat: add Cursor session runtime"
```

If shape pinning is a second commit:

```bash
git commit -m "fix: pin Cursor credential file shape from CLI file store"
```

---

### Task 6: Frontend — agent option, policy UX, credentials

**Files:**
- Modify: `frontend/src/lib/agent.js`
- Modify: `frontend/src/lib/agent.test.js`
- Modify: `frontend/src/components/NewSessionDialog.vue`
- Modify: `frontend/src/components/EditSessionDialog.vue`
- Modify: `frontend/src/components/DuplicateSessionDialog.vue`
- Modify: `frontend/src/components/CredentialsDialog.vue`
- Modify: `frontend/src/components/SessionList.vue` / `SessionsView.vue` / search helpers as needed for labels
- Modify: `frontend/src/components/agent-dialogs.test.js`
- Modify: `frontend/src/components/views.test.js`
- Modify: `frontend/src/lib/text.js` / `text.test.js` if search fields list agents

**Interfaces:**
- `agentOptions` includes `{ value: 'Cursor', label: 'Cursor', hint: 'Cursor agent runtime' }`.
- `defaultPolicy('Cursor')` seeds `Shell(...)` / `Read(...)` style tokens; `allowedCommands` empty; UI hides or disables command-prefix field for Cursor.
- `credentialReadiness` uses `cursorSubscription` / `cursorApiKey`.
- Credentials dialog: write-only Cursor API key + clear `cursorApiKey`.

- [ ] **Step 1: Write failing helper and dialog tests**

```js
it('includes Cursor in agent options', () => {
  expect(agentOptions.map(o => o.value)).toEqual(['Claude', 'Codex', 'Cursor'])
})

it('uses Cursor permission token defaults and empty commands', () => {
  expect(defaultPolicy('Cursor')).toMatchObject({
    allowedTools: expect.arrayContaining(['Read(**)']),
    allowedCommands: []
  })
})

it('creates a Cursor API-key autonomous session', async () => {
  // mount NewSessionDialog, select Cursor + ApiKey, submit
  expect(api.createSession).toHaveBeenCalledWith(expect.objectContaining({
    agent: 'Cursor', authMode: 'ApiKey',
    policy: expect.objectContaining({ allowedCommands: [] })
  }))
})

it('clears Cursor API key without reading it back', async () => {
  await wrapper.get('[data-clear="cursorApiKey"]').trigger('click')
  expect(api.storeCredentials).toHaveBeenCalledWith({ clear: ['cursorApiKey'] })
})
```

Assert Cursor never offers `Auto`. Assert list/detail/search show `Cursor`.

- [ ] **Step 2: Verify RED**

```powershell
Push-Location frontend
npm test -- --run src/lib/agent.test.js src/components/agent-dialogs.test.js src/components/views.test.js
Pop-Location
```

Expected: missing Cursor option / selectors fail.

- [ ] **Step 3: Implement UI**

Update `agent.js` readiness:

```js
if (authMode === 'Subscription') {
  const ready = !!status[
    agent === 'Codex' ? 'codexSubscription'
      : agent === 'Cursor' ? 'cursorSubscription'
      : 'claudeSubscription'
  ]
  // ...
}
const ready = !!status[
  agent === 'Codex' ? 'openAiApiKey'
    : agent === 'Cursor' ? 'cursorApiKey'
    : 'anthropicApiKey'
]
```

In New/Edit dialogs: Cursor placeholders for permission tokens; hide `allowedCommands` textarea when `form.agent === 'Cursor'`. Credentials: password input `data-credential="cursorApiKey"`.

- [ ] **Step 4: Verify GREEN**

```powershell
Push-Location frontend
npm test -- --run
npm run build
Pop-Location
```

Expected: all frontend tests pass; Vite build exits 0.

- [ ] **Step 5: Commit**

```bash
git add frontend/src
git commit -m "feat: select Cursor agent and credentials in UI"
```

---

### Task 7: Helm, plain manifests, setup-dev, and CI for the fifth image

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
- Create: `tests/helm/cursor-runtime-values.ps1`
- Modify: `tests/helm/deployment-parity.ps1` (assert Cursor image in plain manifest)

**Interfaces:**
- Helm: `agent.images.cursor` → helper `agenthub.cursorAgentImage`
- Backend env: `AgentHub__CursorAgentImage`
- Local tags: `open-agenthub-dev/agent-runtime-cursor:local`
- CI matrix adds `agent-runtime-cursor`

- [ ] **Step 1: Write failing rendered-chart assertion**

`tests/helm/cursor-runtime-values.ps1` templates the chart and asserts:

```text
AgentHub__ClaudeAgentImage: "open-agenthub-dev/agent-runtime-claude:local"
AgentHub__CodexAgentImage: "open-agenthub-dev/agent-runtime-codex:local"
AgentHub__CursorAgentImage: "open-agenthub-dev/agent-runtime-cursor:local"
```

Fail if any image value is empty.

- [ ] **Step 2: Verify RED**

```powershell
pwsh -File tests/helm/cursor-runtime-values.ps1
```

Expected: missing helper/value assertion fails.

- [ ] **Step 3: Implement Helm / k8s / scripts / CI**

Add helper analogous to Codex. Update ConfigMap and `k8s/20-backend.yaml`. Extend setup scripts:

```powershell
docker build --file (Join-Path $repoRoot 'agent-runtime/cursor/Dockerfile') --tag 'open-agenthub-dev/agent-runtime-cursor:local' (Join-Path $repoRoot 'agent-runtime')
```

CI `build-images.yml` component matrix:

```yaml
component: [backend, frontend, agent-runtime-claude, agent-runtime-codex, agent-runtime-cursor]
```

`test.yml`: build Cursor image and run any Cursor container smoke script if added.

Keep docker-desktop context refusal unchanged.

- [ ] **Step 4: Verify chart and scripts**

```powershell
helm lint helm/open-agenthub -f helm/open-agenthub/values-dev.yaml --set-string postgres.password=test-only
pwsh -File tests/helm/cursor-runtime-values.ps1
pwsh -File tests/helm/deployment-parity.ps1
git diff --check
```

Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add helm k8s setup-dev.ps1 setup-dev.sh .github tests/helm
git commit -m "build: publish and deploy Cursor agent runtime"
```

---

### Task 8: Documentation and Docker Desktop acceptance

**Files:**
- Modify: `README.md` (and deploy/release docs that list runtime images)
- Create: `docs/testing/cursor-docker-desktop-acceptance.md`
- Modify only if acceptance exposes defects: files from Tasks 1–7, with a new failing regression test first

**Interfaces:**
- Docs describe Cursor selection, both auth modes, third/fifth image build, trusted-code credential boundary, `cursor-state.tgz`, and that allowlists are not a sandbox.
- Acceptance report: non-secret commands, image tags/digests, test counts, resource matrix.

- [ ] **Step 1: Full local verification**

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --nologo
Push-Location frontend; npm ci; npm test -- --run; npm run build; Pop-Location
Push-Location agent-runtime/session-agent; npm ci; npm test; Pop-Location
helm lint helm/open-agenthub -f helm/open-agenthub/values-dev.yaml --set-string postgres.password=test-only
pwsh -File tests/helm/cursor-runtime-values.ps1
git diff --check
```

Expected: every command exits 0. Record exact test counts.

- [ ] **Step 2: Build and deploy on Docker Desktop Kubernetes**

```powershell
kubectl config current-context
```

Expected: exactly `docker-desktop`. If not, stop without switching.

```powershell
.\setup-dev.ps1 -NoPortForward
```

Expected: five images build (backend, frontend, Claude, Codex, Cursor), Helm upgrade succeeds, rollouts healthy.

- [ ] **Step 3: Resource matrix without real secrets**

Create Cursor Interactive / Autonomous / Scheduled sessions (synthetic fixtures via internal test path only). Inspect:

```powershell
kubectl -n agenthub-dev-sessions get pods,cronjobs -o wide
kubectl -n agenthub-dev-sessions get pod <name> -o json
```

Assert only selected Cursor mount or `CURSOR_API_KEY` is present; Claude/Codex creds absent. Never dump Secret data.

- [ ] **Step 4: Behavior checklist**

Verify:

- unauthenticated Cursor Interactive reaches `agent login`;
- missing selected credential fails Autonomous/Scheduled with the standard diagnostic;
- synthetic subscription file create/refresh updates only the Cursor owner Secret;
- API-key mode does not upload a subscription file;
- allowed permission tokens proceed; unmatched tokens deny;
- MCP conversion succeeds; unsupported transport fails before CLI start;
- pause/resume uses `cursor-state.tgz` and does not restore stale credentials over newer Secrets;
- Claude and Codex sessions still start;
- terminal reconnect replays scrollback.

Do not copy a developer workstation's real Cursor credentials into the cluster. Real subscription login is optional and user-performed in the session terminal only.

- [ ] **Step 5: Write acceptance report and fix only test-proven defects**

Write `docs/testing/cursor-docker-desktop-acceptance.md` with environment versions, image tags, test counts, resource matrix, non-secret observations, and any account-bound step left for the user.

- [ ] **Step 6: Final verification and commit**

Repeat Step 1. Then:

```powershell
git status --short
git log --oneline --decorate -10
```

```bash
git add README.md docs/testing/cursor-docker-desktop-acceptance.md
git commit -m "test: verify Cursor support on Docker Desktop Kubernetes"
```

---

## Final self-review checklist

- [ ] Map every acceptance criterion in `docs/superpowers/specs/2026-07-25-cursor-agent-support-design.md` to at least one passing test or acceptance row.
- [ ] Confirm credential filename/JSON shape was discovered from the file store and pinned identically in validator, Secret factory, auth watcher, and tests (no remaining accidental placeholder drift).
- [ ] Search tracked files for token-shaped fixtures; all must be unmistakably synthetic.
- [ ] Inspect rendered Pods: selected-only Secret mounts / `CURSOR_API_KEY`; no Claude/Codex leakage on Cursor sessions.
- [ ] Confirm no real local Cursor credential file was read, copied, logged, or committed.
- [ ] Confirm Helm/CI/setup-dev build all three agent runtimes plus backend/frontend.
- [ ] Confirm docs never claim policy allowlists are a full sandbox and never associate the project with unrelated company brands.
- [ ] Confirm the main checkout remains untouched and all commits are on the Cursor feature branch / worktree.
