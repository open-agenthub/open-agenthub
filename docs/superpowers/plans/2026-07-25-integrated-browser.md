# Integrated On-Demand Browser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add one lazily allocated, session-isolated Chromium browser that agents control through a built-in MCP server and authorized users see through noVNC.

**Architecture:** A local stdio MCP process in each agent pod authenticates to the backend with the existing per-session callback token. The backend authorizes both that token and the caller pod IP, owns a one-per-session browser lease, and creates a hardened browser pod plus exact-match NetworkPolicies. The agent connects directly to CDP while the frontend reaches RFB only through an authenticated backend WebSocket proxy.

**Tech Stack:** ASP.NET Core 10, KubernetesClient 14, PostgreSQL/Npgsql, Node.js 22, MCP TypeScript SDK 1.29.0, Playwright Core 1.61.1, Chromium, Xvfb, x11vnc, websockify, Vue 3, noVNC 1.7.0, Helm.

## Global Constraints

- The browser feature is enabled by default but consumes no browser CPU or memory before the first MCP request.
- Exactly one active browser lease may exist per agent session.
- A session ID alone never authorizes browser lifecycle or access.
- Browser lifecycle authorization requires the matching callback token and the source IP of the live, matching agent pod.
- The browser MCP is stdio-only and user MCP JSON cannot override it.
- CDP TCP 9222 is reachable only from the matching agent pod.
- RFB/websockify is reachable only from the Open AgentHub backend.
- Browser pods are non-root, drop all capabilities, use RuntimeDefault seccomp, have a read-only root filesystem, and mount no service-account token.
- With S3 configured, persist only cookies at `sessions/{owner-hash}/{sessionId}/browser-cookies.json`.
- Session duplication never copies cookies; session deletion removes them.
- The UI has no manual browser start or stop button.
- Desktop uses a resizable 50/50 browser-left/agent-right split; narrow layouts use tabs.
- Viewer shares are view-only; owner and collaborator shares may send input.
- WebRTC, TURN, audio, full browser profiles, downloads, multiple browsers, and session-creation MCP tools are outside this plan.

---

### Task 1: Browser Domain Model and Lease Persistence

**Files:**
- Create: `backend/Browser/BrowserModels.cs`
- Create: `backend/Browser/BrowserLeaseStore.cs`
- Modify: `backend/Storage/S3ArtifactStore.cs`
- Modify: `backend/Program.cs`
- Create: `tests/AgentHub.Api.Tests/BrowserLeaseStoreTests.cs`
- Modify: `tests/AgentHub.Api.Tests/ArtifactStoreKeyTests.cs`

**Interfaces:**
- Produces: `BrowserPhase`, `BrowserSummary`, `BrowserConnection`, `BrowserLease`, `IBrowserLeaseStore`.
- Produces: `IArtifactStore.BrowserCookiesKey(owner, sessionId)` and `DeleteAsync(key, ct)`.
- Consumes later: Tasks 2–4 use the lease store as the serialization point for lifecycle calls.

- [ ] **Step 1: Write failing cookie-key and lease-transition tests**

```csharp
[Fact]
public void BrowserCookiesKey_IsScopedToOwnerAndSession() =>
    Assert.Equal("sessions/u-owner/s1/browser-cookies.json",
        IArtifactStore.BrowserCookiesKey("u-owner", "s1"));

[Fact]
public async Task TryCreate_AllowsOnlyOneActiveLeasePerSession()
{
    var store = new InMemoryBrowserLeaseStore();
    var first = await store.TryCreateAsync(BrowserLease.Pending("s1", "lease-a", Hash("token-a")));
    var second = await store.TryCreateAsync(BrowserLease.Pending("s1", "lease-b", Hash("token-b")));
    Assert.True(first);
    Assert.False(second);
}
```

- [ ] **Step 2: Run the focused tests and verify the missing types fail**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "BrowserLeaseStoreTests|ArtifactStoreKeyTests"
```

Expected: compilation fails because `BrowserCookiesKey`, `BrowserLease`, and `IBrowserLeaseStore` do not exist.

- [ ] **Step 3: Add the domain records and store contract**

```csharp
namespace AgentHub.Api.Browser;

public enum BrowserPhase { Stopped, Pending, Running, Stopping, Failed }

public sealed record BrowserSummary(
    BrowserPhase Phase,
    int ScreenWidth = 1440,
    int ScreenHeight = 900,
    string? FailureCode = null);

public sealed record BrowserConnection(
    BrowserSummary Browser,
    string CdpEndpoint,
    string PodIp);

public sealed record BrowserLease(
    string SessionId,
    string LeaseId,
    byte[] TokenHash,
    BrowserPhase Phase,
    string? PodIp,
    string? FailureCode,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static BrowserLease Pending(string sessionId, string leaseId, byte[] tokenHash) =>
        new(sessionId, leaseId, tokenHash, BrowserPhase.Pending, null, null,
            DateTime.UtcNow, DateTime.UtcNow);
}

public interface IBrowserLeaseStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> TryCreateAsync(BrowserLease lease, CancellationToken ct = default);
    Task<BrowserLease?> GetBySessionAsync(string sessionId, CancellationToken ct = default);
    Task<BrowserLease?> GetByLeaseAsync(string leaseId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, BrowserLease>> ListBySessionsAsync(
        IReadOnlyCollection<string> sessionIds, CancellationToken ct = default);
    Task SetRunningAsync(string leaseId, string podIp, CancellationToken ct = default);
    Task SetFailedAsync(string leaseId, string failureCode, CancellationToken ct = default);
    Task SetStoppingAsync(string leaseId, CancellationToken ct = default);
    Task DeleteAsync(string leaseId, CancellationToken ct = default);
}
```

Implement `PostgresBrowserLeaseStore` with a `browser_leases` table, `session_id` as the
primary key, `lease_id` unique, token hash as `BYTEA`, enum phase as text, optional pod IP
and failure code, and timestamps. Use `INSERT ... ON CONFLICT (session_id) DO NOTHING`
for `TryCreateAsync`.

- [ ] **Step 4: Extend the artifact contract**

```csharp
Task DeleteAsync(string key, CancellationToken ct = default);
static string BrowserCookiesKey(string owner, string id) =>
    $"sessions/{owner}/{id}/browser-cookies.json";
```

`NullArtifactStore.DeleteAsync` returns `Task.CompletedTask`. `S3ArtifactStore.DeleteAsync`
calls `DeleteObjectAsync` and treats a missing object as success.

- [ ] **Step 5: Register and initialize the lease store**

```csharp
builder.Services.AddSingleton<IBrowserLeaseStore, PostgresBrowserLeaseStore>();
// During startup:
await scope.ServiceProvider.GetRequiredService<IBrowserLeaseStore>().InitializeAsync();
```

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "BrowserLeaseStoreTests|ArtifactStoreKeyTests"
```

Expected: all selected tests pass.

Commit:

```powershell
git add backend/Browser backend/Storage/S3ArtifactStore.cs backend/Program.cs tests/AgentHub.Api.Tests
git commit -m "feat(browser): add lease and cookie persistence model"
```

---

### Task 2: Session-Bound Lifecycle Authorization

**Files:**
- Create: `backend/Browser/BrowserRequestAuthorizer.cs`
- Create: `backend/Browser/KubernetesAgentPodIdentityResolver.cs`
- Create: `tests/AgentHub.Api.Tests/BrowserRequestAuthorizerTests.cs`
- Create: `tests/AgentHub.Api.Tests/KubernetesAgentPodIdentityResolverTests.cs`
- Modify: `backend/Program.cs`

**Interfaces:**
- Consumes: `ISessionStore.GetByCallbackTokenAsync`.
- Produces: `IAgentPodIdentityResolver.IsLiveSessionPodAsync(sessionId, sourceIp, ct)`.
- Produces: `IBrowserRequestAuthorizer.AuthorizeAsync(sessionId, token, sourceIp, ct)`.
- Later controllers receive the authorized `SessionRecord`; they never look up by route ID alone.

- [ ] **Step 1: Write the authorization matrix**

```csharp
[Theory]
[InlineData(null, "10.0.0.8", false)]
[InlineData("wrong", "10.0.0.8", false)]
[InlineData("token-s1", "10.0.0.9", false)]
[InlineData("token-s1", "10.0.0.8", true)]
public async Task Authorize_RequiresTokenRouteAndLiveSourcePod(
    string? token, string source, bool expected)
{
    var sessions = new FakeSessionStore(Session("s1", "token-s1"));
    var pods = new FakePodIdentityResolver("s1", "10.0.0.8");
    var auth = new BrowserRequestAuthorizer(sessions, pods);
    var result = await auth.AuthorizeAsync("s1", token, IPAddress.Parse(source));
    Assert.Equal(expected, result is not null);
}

[Fact]
public async Task Authorize_DoesNotTrustForwardedHeaders()
{
    var result = await AuthorizeFromRemoteIp("10.0.0.9",
        forwardedFor: "10.0.0.8", token: "token-s1");
    Assert.Null(result);
}
```

- [ ] **Step 2: Run and observe missing authorizer failure**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "BrowserRequestAuthorizerTests|KubernetesAgentPodIdentityResolverTests"
```

Expected: compilation fails because the authorization interfaces are absent.

- [ ] **Step 3: Implement the pure authorizer**

```csharp
public interface IBrowserRequestAuthorizer
{
    Task<SessionRecord?> AuthorizeAsync(
        string sessionId, string? token, IPAddress sourceIp, CancellationToken ct = default);
}

public sealed class BrowserRequestAuthorizer(
    ISessionStore sessions,
    IAgentPodIdentityResolver pods) : IBrowserRequestAuthorizer
{
    public async Task<SessionRecord?> AuthorizeAsync(
        string sessionId, string? token, IPAddress sourceIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var session = await sessions.GetByCallbackTokenAsync(token, ct);
        if (session is null || !string.Equals(session.Id, sessionId, StringComparison.Ordinal))
            return null;
        return await pods.IsLiveSessionPodAsync(sessionId, sourceIp, ct) ? session : null;
    }
}
```

`KubernetesAgentPodIdentityResolver` lists pods with
`agenthub.dev/session={sessionId},agenthub.dev/component=agent`, accepts only `Running`
pods with a non-empty pod IP, normalizes IPv4-mapped IPv6 addresses, and compares the
request's `RemoteIpAddress`. It reads no forwarded header.

- [ ] **Step 4: Register the services and run tests**

```csharp
builder.Services.AddSingleton<IAgentPodIdentityResolver, KubernetesAgentPodIdentityResolver>();
builder.Services.AddSingleton<IBrowserRequestAuthorizer, BrowserRequestAuthorizer>();
```

Run the focused tests again. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add backend/Browser backend/Program.cs tests/AgentHub.Api.Tests
git commit -m "feat(browser): bind lifecycle auth to the live session pod"
```

---

### Task 3: Hardened Browser Pod and Exact NetworkPolicies

**Files:**
- Create: `backend/Browser/BrowserOptions.cs`
- Create: `backend/Browser/BrowserPodSpecFactory.cs`
- Create: `tests/AgentHub.Api.Tests/BrowserPodSpecFactoryTests.cs`
- Modify: `backend/appsettings.json`

**Interfaces:**
- Produces: `BrowserOptions`.
- Produces: `BrowserPodResources(V1Pod Pod, V1NetworkPolicy CdpPolicy, V1NetworkPolicy VncPolicy)`.
- Consumes later: `KubernetesBrowserService` creates these resources without mutating them.

- [ ] **Step 1: Write security and selector tests**

```csharp
[Fact]
public void Build_UsesRestrictedPodSecurity()
{
    var resources = BrowserPodSpecFactory.Build(Session(), Lease(), Options());
    var pod = resources.Pod.Spec;
    Assert.True(pod.AutomountServiceAccountToken == false);
    Assert.True(pod.SecurityContext.RunAsNonRoot);
    Assert.Equal("RuntimeDefault", pod.SecurityContext.SeccompProfile.Type);
    Assert.All(pod.Containers, c => {
        Assert.False(c.SecurityContext.AllowPrivilegeEscalation);
        Assert.Contains("ALL", c.SecurityContext.Capabilities.Drop);
    });
}

[Fact]
public void Build_CdpPolicy_JoinsOnlyTheExactSession()
{
    var policy = BrowserPodSpecFactory.Build(Session("s1"), Lease(), Options()).CdpPolicy;
    Assert.Equal("s1", policy.Spec.PodSelector.MatchLabels["agenthub.dev/session"]);
    var peer = policy.Spec.Ingress.Single().From.Single();
    Assert.Equal("s1", peer.PodSelector.MatchLabels["agenthub.dev/session"]);
    Assert.Equal("agent", peer.PodSelector.MatchLabels["agenthub.dev/component"]);
    Assert.Equal(9222, policy.Spec.Ingress.Single().Ports.Single().Port.Value);
}
```

- [ ] **Step 2: Run and verify missing factory failure**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter BrowserPodSpecFactoryTests
```

Expected: compilation fails because `BrowserPodSpecFactory` is absent.

- [ ] **Step 3: Implement options and pure resource construction**

```csharp
public sealed class BrowserOptions
{
    public bool Enabled { get; set; } = true;
    public string Image { get; set; } = "ghcr.io/open-agenthub/open-agenthub/browser:latest";
    public string PullPolicy { get; set; } = "IfNotPresent";
    public string CpuRequest { get; set; } = "250m";
    public string MemoryRequest { get; set; } = "512Mi";
    public string CpuLimit { get; set; } = "1";
    public string MemoryLimit { get; set; } = "2Gi";
    public int ScreenWidth { get; set; } = 1440;
    public int ScreenHeight { get; set; } = 900;
    public int StartupTimeoutSeconds { get; set; } = 90;
    public int CookieCheckpointSeconds { get; set; } = 60;
    public int CookieStateMaxBytes { get; set; } = 1_048_576;
    public int[] ExtraEgressPorts { get; set; } = [];
}
```

Build a pod named `browser-{sessionId}` with component `browser`, the opaque session
label, readiness probes for supervisor health and TCP 6080, environment for screen size,
lease ID/token, callback URL, checkpoint interval, and cookie cap. Mount `emptyDir`
volumes at `/data`, `/tmp`, and `/dev/shm`; set `/dev/shm` `SizeLimit` to `512Mi`.

Create two ingress policies:

- `browser-{id}-cdp`: browser selector; source is component `agent` with exact session;
  port 9222.
- `browser-{id}-vnc`: browser selector; source is the control namespace with
  `app=agenthub-backend`; port 6080.

Add browser egress rules for DNS, 80, 443, configured extra ports, and the backend
service on 80/8080 for lease callbacks.

- [ ] **Step 4: Run tests and commit**

Run the focused tests. Expected: all selected tests pass.

```powershell
git add backend/Browser backend/appsettings.json tests/AgentHub.Api.Tests
git commit -m "feat(browser): define hardened browser pod resources"
```

---

### Task 4: Browser Lifecycle, Internal APIs, and Cleanup

**Files:**
- Create: `backend/Browser/IBrowserService.cs`
- Create: `backend/Browser/KubernetesBrowserService.cs`
- Create: `backend/Controllers/BrowserController.cs`
- Create: `backend/Controllers/BrowserLeaseController.cs`
- Create: `backend/Browser/BrowserReconcileService.cs`
- Modify: `backend/Services/ISessionService.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`
- Modify: `backend/Models/SessionModels.cs`
- Modify: `backend/Persistence/PostgresSessionStore.cs`
- Modify: `backend/Controllers/InternalController.cs`
- Modify: `ee/backend/Sharing/ShareModels.cs`
- Modify: `backend/Program.cs`
- Create: `tests/AgentHub.Api.Tests/BrowserControllerTests.cs`
- Create: `tests/AgentHub.Api.Tests/KubernetesBrowserServiceTests.cs`
- Modify: `tests/AgentHub.Api.Tests/SessionStatusTests.cs`

**Interfaces:**
- Produces: `IBrowserService.EnsureAsync`, `GetSummaryAsync`, `GetConnectionAsync`,
  `StopAsync`, `MintStateUrlsAsync`, and `DeleteStateAsync`.
- Produces: internal lifecycle and lease-state HTTP endpoints.
- Extends `SessionInfo` with `BrowserSummary Browser`.

- [ ] **Step 1: Write controller security and idempotency tests**

```csharp
[Fact]
public async Task Start_ReturnsUnauthorized_WhenOnlySessionIdIsKnown()
{
    var controller = Controller(authorize: null);
    Assert.IsType<UnauthorizedResult>(await controller.Start("s1", CancellationToken.None));
}

[Fact]
public async Task Start_UsesAuthorizedSessionAndReturnsOneConnection()
{
    var browser = new RecordingBrowserService(Connection("s1"));
    var controller = Controller(Session("s1"), browser);
    var result = await controller.Start("s1", CancellationToken.None);
    Assert.IsType<OkObjectResult>(result);
    Assert.Equal(1, browser.EnsureCalls);
}

[Fact]
public async Task ConcurrentEnsure_CreatesOnePodAndReturnsOneLease()
{
    var service = ServiceWithRecordingKubernetes();
    await Task.WhenAll(service.EnsureAsync(Session()), service.EnsureAsync(Session()));
    Assert.Single(service.Kubernetes.CreatedPods);
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter "BrowserControllerTests|KubernetesBrowserServiceTests|SessionStatusTests"
```

Expected: compilation fails because lifecycle types and endpoints are absent.

- [ ] **Step 3: Implement the lifecycle service contract**

```csharp
public interface IBrowserService
{
    Task<BrowserConnection> EnsureAsync(SessionRecord session, CancellationToken ct = default);
    Task<BrowserSummary> GetSummaryAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(
        IReadOnlyCollection<string> sessionIds, CancellationToken ct = default);
    Task<BrowserConnection?> GetConnectionAsync(string sessionId, CancellationToken ct = default);
    Task StopAsync(string sessionId, CancellationToken ct = default);
    Task<BrowserStateUrls?> MintStateUrlsAsync(
        string leaseId, string token, CancellationToken ct = default);
    Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default);
}
```

`EnsureAsync` creates a 32-byte random lease token, stores only SHA-256, creates the
pod and policies, waits until pod phase is running and ports 9222/6080 are ready, then
sets the lease running. Treat Kubernetes `409 Conflict` as an idempotent read. On error,
delete partial resources and set one of `image_pull`, `unschedulable`, `cdp_unavailable`,
`vnc_unavailable`, or `startup_timeout`.

`MintStateUrlsAsync` hashes the supplied browser token, compares it with
`CryptographicOperations.FixedTimeEquals`, and returns empty URLs for `NullArtifactStore`
or fresh 15-minute GET/PUT URLs for the exact cookie key.

- [ ] **Step 4: Implement internal controllers**

```csharp
[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/browser")]
public sealed class BrowserController(
    IBrowserRequestAuthorizer authorizer,
    IBrowserService browsers) : ControllerBase
{
    private async Task<SessionRecord?> Authorized(string id, CancellationToken ct)
    {
        Request.Headers.TryGetValue("X-Agent-Token", out var token);
        var source = HttpContext.Connection.RemoteIpAddress;
        return source is null ? null :
            await authorizer.AuthorizeAsync(id, token.FirstOrDefault(), source, ct);
    }

    [HttpPost]
    public async Task<IActionResult> Start(string id, CancellationToken ct) =>
        await Authorized(id, ct) is { } session
            ? Ok(await browsers.EnsureAsync(session, ct))
            : Unauthorized();

    [HttpGet]
    public async Task<IActionResult> Status(string id, CancellationToken ct) =>
        await Authorized(id, ct) is not null
            ? Ok(await browsers.GetSummaryAsync(id, ct))
            : Unauthorized();

    [HttpDelete]
    public async Task<IActionResult> Stop(string id, CancellationToken ct)
    {
        if (await Authorized(id, ct) is null) return Unauthorized();
        await browsers.StopAsync(id, ct);
        return NoContent();
    }
}
```

`BrowserLeaseController` accepts `X-Browser-Token`, caps its length, and returns only the
matching lease's presigned state URLs.

- [ ] **Step 5: Integrate session summaries and cleanup**

Add:

```csharp
public BrowserSummary Browser { get; init; } = new(BrowserPhase.Stopped);
```

Inject `IBrowserService` into `KubernetesSessionService`. List and get operations fetch
lease summaries in one store query. Pause, terminal success/failure callback, resume
pre-cleanup, and deletion call `StopAsync`. Deletion additionally calls
`DeleteStateAsync`; duplication never does.

`InternalController.Status` calls `StopAsync` after accepting `Succeeded` or `Failed`.
The shared-session DTO sanitizer copies only the public `BrowserSummary`.

`BrowserReconcileService` runs once after application startup and then every five
minutes. It removes browser pods and policies whose session or active lease no longer
exists, marks missing pods failed, and never creates a browser.

- [ ] **Step 6: Register services, run tests, and commit**

```csharp
builder.Services.AddSingleton<IBrowserService, KubernetesBrowserService>();
builder.Services.AddHostedService<BrowserReconcileService>();
```

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj
```

Expected: 0 failed tests.

Commit:

```powershell
git add backend tests/AgentHub.Api.Tests
git commit -m "feat(browser): orchestrate authenticated browser lifecycle"
```

---

### Task 5: Authenticated noVNC WebSocket Proxy

**Files:**
- Create: `backend/WebSockets/BrowserProxy.cs`
- Modify: `backend/Program.cs`
- Create: `tests/AgentHub.Api.Tests/BrowserProxyTests.cs`

**Interfaces:**
- Consumes: `ISessionAccessService`, `IBrowserService.GetConnectionAsync`.
- Produces: `/ws/sessions/{id}/browser` and `/ws/shared/{token}/browser`.

- [ ] **Step 1: Write access and read-only pump tests**

```csharp
[Fact]
public async Task Viewer_DoesNotPumpClientInputUpstream()
{
    var result = await BrowserProxyHarness.Run(canWrite: false, clientFrames: ["key"]);
    Assert.Empty(result.UpstreamFrames);
    Assert.Equal(["screen"], result.ClientFrames);
}

[Fact]
public async Task Collaborator_PumpsBothDirections()
{
    var result = await BrowserProxyHarness.Run(canWrite: true, clientFrames: ["key"]);
    Assert.Equal(["key"], result.UpstreamFrames);
}
```

- [ ] **Step 2: Run and verify the missing proxy failure**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter BrowserProxyTests
```

Expected: compilation fails because `BrowserProxy` is absent.

- [ ] **Step 3: Implement the RFB proxy**

Connect upstream to `ws://{browser.PodIp}:6080/` with subprotocol `binary`. Always pump
browser-to-client. Pump client-to-browser only when
`SessionAccessRules.CanWriteTerminal(access.Level)` is true. Close with
`EndpointUnavailable` when no running browser exists. Log the session ID and failure
code, never the upstream URI.

Map authenticated direct-user and shared-link routes with the same access resolution
used by terminal sharing.

- [ ] **Step 4: Run tests and commit**

Run the focused tests, then the complete backend suite. Expected: 0 failures.

```powershell
git add backend/WebSockets/BrowserProxy.cs backend/Program.cs tests/AgentHub.Api.Tests
git commit -m "feat(browser): proxy authorized noVNC sessions"
```

---

### Task 6: Built-in Browser MCP for Claude and Codex

**Files:**
- Modify: `agent-runtime/session-agent/package.json`
- Modify: `agent-runtime/session-agent/package-lock.json`
- Create: `agent-runtime/browser/client.mjs`
- Create: `agent-runtime/browser/snapshot.mjs`
- Create: `agent-runtime/browser/server.mjs`
- Create: `agent-runtime/browser/configure-claude.mjs`
- Modify: `agent-runtime/common/entrypoint-common.sh`
- Modify: `agent-runtime/claude/driver.js`
- Modify: `agent-runtime/codex/entrypoint.sh`
- Modify: `agent-runtime/claude/Dockerfile`
- Modify: `agent-runtime/codex/Dockerfile`
- Modify: `backend/Services/AgentPodSpecFactory.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`
- Modify: `tests/AgentHub.Api.Tests/AgentPodSpecFactoryTests.cs`
- Create: `agent-runtime/session-agent/test/browser-client.test.js`
- Create: `agent-runtime/session-agent/test/browser-snapshot.test.js`
- Create: `agent-runtime/session-agent/test/browser-mcp-config.test.js`
- Modify: `agent-runtime/session-agent/test/claude-driver.test.js`
- Modify: `agent-runtime/session-agent/test/codex-driver.test.js`

**Interfaces:**
- Consumes: internal browser lifecycle API and CDP endpoint.
- Produces MCP server name `agenthub_browser`.
- Produces tools: `browser_start`, `browser_status`, `browser_stop`,
  `browser_navigate`, `browser_snapshot`, `browser_click`, `browser_type`,
  `browser_screenshot`, `browser_tabs`.

- [ ] **Step 1: Install pinned MCP and CDP dependencies**

Run:

```powershell
npm install --save-exact @modelcontextprotocol/sdk@1.29.0 playwright-core@1.61.1 zod@4.1.12
```

Expected: package and lock files contain exact versions and no browser binary download.

- [ ] **Step 2: Write failing client, snapshot, and config tests**

```javascript
test('lifecycle sends the session token only in X-Agent-Token', async () => {
  const calls = [];
  const client = new BrowserBackendClient(env(), async (url, init) => {
    calls.push({ url, init });
    return Response.json(connection());
  });
  await client.start();
  assert.equal(calls[0].init.headers['X-Agent-Token'], 'secret');
  assert.doesNotMatch(calls[0].url, /secret/);
});

test('snapshot refs fail after the page revision changes', async () => {
  const refs = new SnapshotRefs();
  const ref = refs.remember({ selector: '#submit' });
  refs.advanceRevision();
  assert.throws(() => refs.resolve(ref), /stale/i);
});

test('built-in MCP cannot be replaced by user config', () => {
  const merged = mergeClaudeMcp({ mcpServers: {
    agenthub_browser: { command: 'attacker' }, docs: { command: 'docs' }
  }});
  assert.equal(merged.mcpServers.agenthub_browser.command, 'node');
  assert.equal(merged.mcpServers.docs.command, 'docs');
});
```

- [ ] **Step 3: Run tests and verify failure**

Run:

```powershell
npm test
```

Expected: new browser tests fail because the modules are absent.

- [ ] **Step 4: Implement backend and CDP clients**

`BrowserBackendClient` validates required environment, applies a 64 KiB response cap,
sends the callback token only as `X-Agent-Token`, and exposes `start/status/stop`.

`BrowserSession` uses `chromium.connectOverCDP(endpoint)`, selects the default context,
tracks the active page and monotonically increasing revision, and disconnects without
closing remote Chromium.

Snapshot references use opaque `e{revision}-{counter}` identifiers mapped to locators.
Page navigation, tab activation, and main-frame navigation advance the revision.
`browser_click` and `browser_type` resolve only current-revision references.

- [ ] **Step 5: Register all MCP tools**

```javascript
const server = new McpServer({ name: 'agenthub_browser', version: '1.0.0' });

server.registerTool('browser_start', {
  description: 'Start this agent session’s isolated visible Chromium browser.',
  inputSchema: z.object({})
}, async () => text(await browser.start()));

server.registerTool('browser_navigate', {
  description: 'Navigate the active browser tab to an HTTP or HTTPS URL.',
  inputSchema: z.object({ url: z.string().url() })
}, async ({ url }) => text(await browser.navigate(url)));
```

Register the remaining tools with bounded strings, integer tab indexes, explicit action
enums, and MCP image content for screenshots. Sanitize thrown errors into stable codes;
never include callback tokens or cookie values.

- [ ] **Step 6: Configure Claude and Codex automatically**

`configure-claude.mjs` writes `/tmp/agenthub-mcp.json`, merges user servers, and replaces
any user-supplied `agenthub_browser` entry with:

```json
{
  "command": "node",
  "args": ["/opt/session-agent/browser/server.mjs"]
}
```

The common entrypoint sets `AGENTHUB_MCP_CONFIG=/tmp/agenthub-mcp.json`.
Claude's driver uses that path whenever `AGENTHUB_BROWSER_ENABLED=1` or user MCP exists.

Codex appends:

```toml
[mcp_servers.agenthub_browser]
command = "node"
args = ["/opt/session-agent/browser/server.mjs"]
```

after converted user configuration so the runtime-owned table is authoritative.

`AgentPodSpecFactory` injects `AGENTHUB_BROWSER_ENABLED=1` when `Browser:Enabled` is true.
`KubernetesSessionService` maps that option into `AgentPodRuntimeSettings.BrowserEnabled`.
Both provider Dockerfiles copy the new `browser/` runtime directory into
`/opt/session-agent/browser`, including the custom-image runtime injection path.

- [ ] **Step 7: Run runtime tests and commit**

Run:

```powershell
npm test
```

Expected: all runtime tests pass.

```powershell
git add agent-runtime
git commit -m "feat(browser): add built-in browser MCP tools"
```

---

### Task 7: Browser Image and Cookie Supervisor

**Files:**
- Create: `browser-runtime/package.json`
- Create: `browser-runtime/package-lock.json`
- Create: `browser-runtime/Dockerfile`
- Create: `browser-runtime/entrypoint.sh`
- Create: `browser-runtime/supervisor.mjs`
- Create: `browser-runtime/test/supervisor.test.mjs`
- Create: `browser-runtime/smoke.ps1`

**Interfaces:**
- Exposes: CDP on TCP 9222, RFB WebSocket on TCP 6080, health on TCP 6081.
- Consumes: lease callback URL/token and cookie limits from environment.

- [ ] **Step 1: Create the pinned runtime package and failing tests**

Use exact dependency:

```json
{
  "name": "open-agenthub-browser-runtime",
  "private": true,
  "type": "module",
  "scripts": { "test": "node --test" },
  "dependencies": { "playwright-core": "1.61.1" }
}
```

Tests cover cookie schema filtering, the 1 MiB cap, token header placement, corrupt
restore fallback, and final checkpoint behavior.

- [ ] **Step 2: Implement the supervisor**

The supervisor waits for `http://127.0.0.1:9223/json/version`, connects with
Playwright Core, imports only valid browser cookies, starts the checkpoint timer, and
serves `/healthz` on 6081. Each checkpoint requests fresh 15-minute GET/PUT URLs from:

`GET {AGENTHUB_BROWSER_CALLBACK_URL}/state-urls`

using `X-Browser-Token`. On `SIGTERM`, stop the timer, checkpoint once with a bounded
10-second timeout, disconnect Playwright, and exit.

- [ ] **Step 3: Implement the non-root image**

Base on `node:22-bookworm-slim`. Install pinned Debian repository versions available at
build time for `chromium`, `xvfb`, `x11vnc`, `websockify`, `socat`, `curl`, and CA
certificates. Create UID/GID 1000, copy the runtime, run `npm ci --omit=dev`, and switch
to UID 1000.

The entrypoint launches:

```bash
Xvfb :99 -screen 0 "${AGENTHUB_BROWSER_SCREEN:-1440x900}x24" -nolisten tcp
chromium --display=:99 --remote-debugging-port=9223 \
  --user-data-dir=/data/chromium --no-first-run --disable-background-networking about:blank
x11vnc -display :99 -localhost -forever -shared -nopw -rfbport 5900
websockify 0.0.0.0:6080 127.0.0.1:5900
socat TCP-LISTEN:9222,fork,reuseaddr TCP:127.0.0.1:9223
node /opt/browser/supervisor.mjs
```

Track child PIDs, forward termination, and fail when any required child exits.

- [ ] **Step 4: Run unit and container smoke tests**

Run:

```powershell
npm test
docker build -t open-agenthub-browser:test .
docker run --rm --name agenthub-browser-smoke -p 19222:9222 -p 16080:6080 open-agenthub-browser:test
```

The smoke script checks `/json/version`, performs a WebSocket upgrade on 6080, confirms
UID 1000, and confirms the container has no writable root paths beyond mounted runtime
directories.

- [ ] **Step 5: Commit**

```powershell
git add browser-runtime
git commit -m "feat(browser): add hardened visible Chromium runtime"
```

---

### Task 8: Vue noVNC Split View

**Files:**
- Modify: `frontend/package.json`
- Modify: `frontend/package-lock.json`
- Modify: `frontend/src/api.js`
- Create: `frontend/src/components/BrowserPane.vue`
- Create: `frontend/src/components/SessionWorkspace.vue`
- Modify: `frontend/src/components/TerminalView.vue`
- Create: `frontend/src/components/browser-workspace.test.js`
- Modify: `frontend/src/components/terminal-sharing.test.js`

**Interfaces:**
- Consumes: `session.browser` summary and browser WebSocket routes.
- Produces: automatic desktop split, resizer, narrow-layout tabs, and view-only RFB.

- [ ] **Step 1: Install noVNC and write failing UI tests**

Run:

```powershell
npm install --save-exact @novnc/novnc@1.7.0
```

Test:

```javascript
it('does not render browser chrome while stopped', () => {
  const wrapper = mountWorkspace({ browser: { phase: 'Stopped' } })
  expect(wrapper.find('[data-browser-pane]').exists()).toBe(false)
  expect(wrapper.text()).not.toContain('Start browser')
})

it('opens the 50/50 split when the agent starts a browser', async () => {
  const wrapper = mountWorkspace({ browser: { phase: 'Running' } })
  expect(wrapper.get('[data-browser-pane]').exists()).toBe(true)
  expect(wrapper.get('[data-session-split]').attributes('style')).toContain('50%')
})

it('sets noVNC viewOnly for viewers', () => {
  const rfb = mountBrowser({ canWrite: false }).rfb
  expect(rfb.viewOnly).toBe(true)
})
```

- [ ] **Step 2: Run tests and verify failure**

Run:

```powershell
npm test -- browser-workspace.test.js terminal-sharing.test.js
```

Expected: tests fail because the browser components and URL helpers are absent.

- [ ] **Step 3: Add URL helpers and BrowserPane**

```javascript
export const browserUrl = (id) => wsUrl(id, 'browser')
export const sharedBrowserUrl = (token) =>
  `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}` +
  `/ws/shared/${encodeURIComponent(token)}/browser`
```

`BrowserPane` creates one `RFB` instance on mount, sets `scaleViewport=true`,
`resizeSession=false`, `viewOnly=!canWrite`, reconnects with bounded exponential delay,
and disconnects on unmount. It displays pending, reconnecting, and sanitized failed
states but has no lifecycle controls.

- [ ] **Step 4: Add the adaptive session workspace**

`SessionWorkspace` owns `splitPercent`, default 50, clamps 25–75, implements a keyboard
accessible separator with Left/Right arrows, and chooses tabs under 760 px. It renders
the browser left and the existing terminal content right. Preserve terminal component
instances when switching narrow-layout tabs so PTY connections do not reconnect.

Refactor `TerminalView` so its existing top bar remains full-width and the content below
is supplied to `SessionWorkspace`.

- [ ] **Step 5: Run tests, build, and commit**

Run:

```powershell
npm test
npm run build
```

Expected: all tests pass and Vite builds production assets.

```powershell
git add frontend
git commit -m "feat(browser): show the live browser beside agent sessions"
```

---

### Task 9: Helm, Plain Manifests, CI, and Local Development

**Files:**
- Modify: `helm/open-agenthub/values.yaml`
- Modify: `helm/open-agenthub/values-dev.yaml`
- Modify: `helm/open-agenthub/templates/configmap.yaml`
- Modify: `helm/open-agenthub/templates/rbac.yaml`
- Modify: `helm/open-agenthub/templates/networkpolicy.yaml`
- Modify: `k8s/10-rbac.yaml`
- Modify: `k8s/20-backend.yaml`
- Modify: `k8s/30-networkpolicy.yaml`
- Modify: `.github/workflows/build-images.yml`
- Modify: `.github/workflows/test.yml`
- Modify: `setup-dev.ps1`
- Modify: `setup-dev.sh`
- Modify: `tests/helm/deployment-parity.ps1`
- Create: `tests/helm/browser-values.ps1`

**Interfaces:**
- Maps the accepted `browser:` Helm values to `Browser__*` backend configuration.
- Grants backend create/get/list/watch/delete only for NetworkPolicies in the session namespace.

- [ ] **Step 1: Write failing Helm parity assertions**

```powershell
Assert-Contains $rendered 'Browser__Enabled: "true"'
Assert-Contains $rendered 'Browser__ScreenWidth: "1440"'
Assert-Contains $rendered 'resources: ["networkpolicies"]'
Assert-Contains $rendered 'verbs: ["get", "list", "watch", "create", "delete"]'
```

Also render with `--set browser.enabled=false` and assert the config is false while
existing agent policies remain present.

- [ ] **Step 2: Run Helm tests and verify failure**

Run:

```powershell
pwsh tests/helm/browser-values.ps1
pwsh tests/helm/deployment-parity.ps1
```

Expected: the new browser assertions fail.

- [ ] **Step 3: Add chart and manifest configuration**

Add the exact `browser:` defaults from the design spec. Map image, pull policy, resources,
screen dimensions, timeouts, checkpoint interval, state cap, and extra egress ports into
the backend ConfigMap.

Extend session-manager RBAC:

```yaml
- apiGroups: ["networking.k8s.io"]
  resources: ["networkpolicies"]
  verbs: ["get", "list", "watch", "create", "delete"]
```

Do not add 9222 to the existing broad agent egress policy; dynamic per-session policy
provides that access.

- [ ] **Step 4: Add image builds and development wiring**

Add matrix component `browser` with context `./browser-runtime`. Build
`open-agenthub-dev/browser:local` in both setup scripts and set the development Browser
image value. Add browser runtime unit tests to the test workflow.

- [ ] **Step 5: Run render tests and commit**

Run:

```powershell
pwsh tests/helm/browser-values.ps1
pwsh tests/helm/deployment-parity.ps1
helm template agenthub helm/open-agenthub --set postgres.password=test
```

Expected: all scripts pass and Helm renders without warnings or invalid resources.

```powershell
git add helm k8s .github setup-dev.ps1 setup-dev.sh tests/helm
git commit -m "feat(browser): deploy browser orchestration defaults"
```

---

### Task 10: Documentation and End-to-End Verification

**Files:**
- Modify: `README.md`
- Create: `docs/browser-security.md`
- Create: `tests/browser-smoke.ps1`
- Modify: `docs/superpowers/specs/2026-07-25-integrated-browser-design.md` only if implementation names differ while preserving approved behavior.

**Interfaces:**
- Documents operator configuration, trust boundaries, cookie sensitivity, and diagnostic commands.
- Produces one repeatable Docker Desktop smoke test.

- [ ] **Step 1: Add operator and security documentation**

Document:

- lazy lifecycle and automatic MCP availability;
- exact MCP tool names;
- no manual UI lifecycle controls;
- callback-token plus source-pod authorization;
- CDP and RFB NetworkPolicy boundaries;
- S3 cookie object contents, deletion behavior, and encryption-at-rest requirement;
- browser resource values and disable switch;
- troubleshooting for pending, failed, CDP unavailable, and VNC unavailable states.

- [ ] **Step 2: Implement the Docker Desktop smoke**

The script must:

1. refuse any Kubernetes context other than `docker-desktop`;
2. create a live test session through the dev API;
3. discover its callback token only through a test-only in-cluster fixture;
4. call browser lifecycle from the matching pod and expect `200`;
5. call it from a foreign labeled pod with the same session ID but no token and expect
   `401`;
6. call it from a foreign pod with the token and expect `401` due to source-IP binding;
7. verify matching pod CDP access and foreign pod CDP denial;
8. verify browser WebSocket access through the backend;
9. pause the session and verify pod, policies, and lease cleanup;
10. when MinIO is configured, resume and verify a test cookie is restored.

- [ ] **Step 3: Run all automated verification**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj
npm test
npm run build
```

Run `npm test` from both `agent-runtime/session-agent` and `browser-runtime`.

Run:

```powershell
pwsh tests/helm/browser-values.ps1
pwsh tests/helm/deployment-parity.ps1
helm lint helm/open-agenthub --set postgres.password=test
helm template agenthub helm/open-agenthub --set postgres.password=test
git diff --check
```

Expected: all test suites, builds, lint, render, and diff checks pass.

- [ ] **Step 4: Run environment-dependent verification when available**

Run:

```powershell
docker build -t open-agenthub-browser:test browser-runtime
pwsh browser-runtime/smoke.ps1
pwsh tests/browser-smoke.ps1
```

If Docker Desktop Kubernetes is unavailable, record those two checks as not run rather
than claiming they passed.

- [ ] **Step 5: Review security-sensitive output**

Search tracked files and test output for callback tokens, browser lease tokens, private
CDP URLs, and cookie contents. Confirm the public `SessionInfo` JSON contains only the
approved browser summary.

- [ ] **Step 6: Commit final documentation and smoke coverage**

```powershell
git add README.md docs tests/browser-smoke.ps1
git commit -m "docs(browser): document operation and security boundaries"
```
