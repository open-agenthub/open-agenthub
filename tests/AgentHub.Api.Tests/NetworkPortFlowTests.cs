using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Network;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The port-request lifecycle: agent-side endpoint (X-Agent-Token auth, allowlist
/// enforcement, pending request on the permission channel) and policy application
/// once the request is decided.
/// </summary>
public class NetworkPortFlowTests
{
    private const string SessionId = "session-1";
    private const string Token = "callback-token";

    // ------------------------------------------------------------- no-Postgres paths

    [Fact]
    public async Task RequestPort_RejectsUnauthenticatedCallers()
    {
        var fixture = Fixture(DisconnectedPermissionStore());
        var controller = fixture.Controller(token: null);

        var result = await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, null, "reach db"), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(fixture.Policies.Created);
    }

    [Fact]
    public async Task RequestPort_RejectsWrongToken()
    {
        var fixture = Fixture(DisconnectedPermissionStore());
        var controller = fixture.Controller(token: "some-other-token");

        var result = await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, null, "reach db"), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Theory]
    [InlineData("sideways", 5432, "TCP")]
    [InlineData("egress", 0, "TCP")]
    [InlineData("egress", 65536, "TCP")]
    [InlineData("egress", 5432, "ICMP")]
    [InlineData("browser_to_agent", 3000, "UDP")] // browser talks HTTP — TCP only
    public async Task RequestPort_ValidatesTheInput(string direction, int port, string protocol)
    {
        var fixture = Fixture(DisconnectedPermissionStore());
        var controller = fixture.Controller();

        var result = await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody(direction, port, protocol, "why"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(fixture.Policies.Created);
    }

    [Fact]
    public async Task RequestPort_OutsideAllowlistIsDeniedWithoutAskingAnyone()
    {
        // The store would throw on any query — proving nobody is asked and nothing is stored.
        var fixture = Fixture(DisconnectedPermissionStore());
        var controller = fixture.Controller();

        var result = await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 22, "TCP", "ssh somewhere"), CancellationToken.None);

        var json = Json(result);
        Assert.Equal("deny", json.GetProperty("decision").GetString());
        Assert.Contains("allowlist", json.GetProperty("reason").GetString());
        Assert.Empty(fixture.Policies.Created);
        Assert.Empty(fixture.Grants.All);
    }

    [Fact]
    public async Task RequestPort_DisabledInstanceDeniesEverything()
    {
        var fixture = Fixture(DisconnectedPermissionStore(), enabled: false);
        var controller = fixture.Controller();

        var result = await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, "TCP", "reach db"), CancellationToken.None);

        Assert.Equal("deny", Json(result).GetProperty("decision").GetString());
        Assert.Empty(fixture.Policies.Created);
    }

    [Fact]
    public async Task RequestPort_AutoApproveAppliesThePolicyImmediately()
    {
        var fixture = Fixture(DisconnectedPermissionStore(), autoApprove: true);
        var controller = fixture.Controller();

        var result = await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, "TCP", "reach db"), CancellationToken.None);

        Assert.Equal("allow", Json(result).GetProperty("decision").GetString());
        var policy = Assert.Single(fixture.Policies.Created);
        Assert.Equal($"session-{SessionId}-net-egress-tcp-5432-out", policy.Metadata.Name);
        Assert.Equal("test-sessions", policy.Metadata.NamespaceProperty);
        var grant = Assert.Single(fixture.Grants.All);
        Assert.Equal((SessionId, PortDirection.Egress, 5432, "TCP"), (grant.SessionId, grant.Direction, grant.Port, grant.Protocol));
    }

    [Fact]
    public async Task Cleanup_DeletesPoliciesAndGrants()
    {
        var fixture = Fixture(DisconnectedPermissionStore());
        await fixture.Grants.AddAsync(SessionId, PortDirection.Egress, 5432, "TCP");

        await fixture.Service.CleanupSessionAsync(SessionId);

        Assert.Equal([("test-sessions", SessionId)], fixture.Policies.DeletedSessions);
        Assert.Empty(fixture.Grants.All);
    }

    [Fact]
    public async Task ListPorts_ReturnsTheSessionGrants()
    {
        var fixture = Fixture(DisconnectedPermissionStore());
        await fixture.Grants.AddAsync(SessionId, PortDirection.BrowserToAgent, 3000, "TCP");
        var controller = fixture.Controller();

        var json = Json(await controller.ListPorts(SessionId, CancellationToken.None));

        var port = Assert.Single(json.GetProperty("ports").EnumerateArray());
        Assert.Equal("browser_to_agent", port.GetProperty("direction").GetString());
        Assert.Equal(3000, port.GetProperty("port").GetInt32());
        Assert.Equal("TCP", port.GetProperty("protocol").GetString());
    }

    // ------------------------------------------------------------- Postgres-backed flow

    [PostgreSqlFact]
    public async Task RequestPort_CreatesAPendingPermissionRequest()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var fixture = Fixture(database.Store);
        var controller = fixture.Controller();

        var result = await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, null, "Connect to staging Postgres."), CancellationToken.None);

        var id = Json(result).GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        var pending = Assert.Single(await database.Store.GetPendingRequestsAsync(SessionId));
        Assert.Equal(id, pending.Id);
        Assert.Equal("NetworkPort(egress 5432/TCP)", pending.Tool);
        Assert.Equal("Connect to staging Postgres.", pending.Summary);
        // Nothing is opened while the request is pending.
        Assert.Empty(fixture.Policies.Created);
        Assert.Equal("pending", Json(await controller.PortRequestStatus(SessionId, id!, CancellationToken.None))
            .GetProperty("decision").GetString());
    }

    [PostgreSqlFact]
    public async Task Approval_AppliesThePoliciesOnTheNextPoll()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var fixture = Fixture(database.Store);
        var controller = fixture.Controller();
        var id = Json(await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("browser_to_agent", 9000, "TCP", "Show the dev server."), CancellationToken.None))
            .GetProperty("id").GetString()!;

        // The owner approves on any surface (web app, Slack, …) — same store either way.
        await database.Store.ResolveAsync(id, "allow", SessionId);

        var decision = Json(await controller.PortRequestStatus(SessionId, id, CancellationToken.None))
            .GetProperty("decision").GetString();
        Assert.Equal("allow", decision);
        Assert.Equal(2, fixture.Policies.Created.Count); // ingress on agent + egress on browser
        Assert.Contains(fixture.Policies.Created, p => p.Metadata.Name.EndsWith("-in"));
        Assert.Contains(fixture.Policies.Created, p => p.Metadata.Name.EndsWith("-out"));
        Assert.Single(fixture.Grants.All);

        // Re-polling is idempotent: no duplicate grants.
        await controller.PortRequestStatus(SessionId, id, CancellationToken.None);
        Assert.Single(fixture.Grants.All);
    }

    [PostgreSqlFact]
    public async Task Denial_OpensNothing()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var fixture = Fixture(database.Store);
        var controller = fixture.Controller();
        var id = Json(await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, "TCP", "reach db"), CancellationToken.None))
            .GetProperty("id").GetString()!;

        await database.Store.ResolveAsync(id, "deny", SessionId);

        Assert.Equal("deny", Json(await controller.PortRequestStatus(SessionId, id, CancellationToken.None))
            .GetProperty("decision").GetString());
        Assert.Empty(fixture.Policies.Created);
        Assert.Empty(fixture.Grants.All);
    }

    [PostgreSqlFact]
    public async Task AllowAlways_AppliesAndShortCircuitsTheNextRequest()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var fixture = Fixture(database.Store);
        var controller = fixture.Controller();
        var id = Json(await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, "TCP", "reach db"), CancellationToken.None))
            .GetProperty("id").GetString()!;

        await database.Store.ResolveAsync(id, "allowAlways", SessionId);
        Assert.Equal("allow", Json(await controller.PortRequestStatus(SessionId, id, CancellationToken.None))
            .GetProperty("decision").GetString());

        // The same port is answered immediately, without a new pending request.
        var again = Json(await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, "TCP", "reach db again"), CancellationToken.None));
        Assert.Equal("allow", again.GetProperty("decision").GetString());
        Assert.Empty(await database.Store.GetPendingRequestsAsync(SessionId));
    }

    [PostgreSqlFact]
    public async Task Expire_MarksTheRequestButHonorsARacingApproval()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var fixture = Fixture(database.Store);
        var controller = fixture.Controller();
        var id = Json(await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 5432, "TCP", "reach db"), CancellationToken.None))
            .GetProperty("id").GetString()!;

        Assert.Equal("expired", Json(await controller.ExpirePortRequest(SessionId, id, CancellationToken.None))
            .GetProperty("decision").GetString());
        Assert.Empty(fixture.Policies.Created);

        // Race in the other direction: decided first, expire afterwards → decision wins.
        var second = Json(await controller.RequestPort(SessionId,
            new NetworkPortsController.PortRequestBody("egress", 3306, "TCP", "reach mysql"), CancellationToken.None))
            .GetProperty("id").GetString()!;
        await database.Store.ResolveAsync(second, "allow", SessionId);
        Assert.Equal("allow", Json(await controller.ExpirePortRequest(SessionId, second, CancellationToken.None))
            .GetProperty("decision").GetString());
        Assert.Single(fixture.Policies.Created);
    }

    [PostgreSqlFact]
    public async Task Decision_ForAForeignRequestIdIsExpired()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        // A request of ANOTHER session must not be readable through this session's token.
        await database.Store.CreateAsync(new PermissionRequest
        {
            Id = "foreign", SessionId = "other-session", Owner = "bob", Tool = "NetworkPort(egress 5432/TCP)"
        });
        var fixture = Fixture(database.Store);
        var controller = fixture.Controller();

        Assert.Equal("expired", Json(await controller.PortRequestStatus(SessionId, "foreign", CancellationToken.None))
            .GetProperty("decision").GetString());
        Assert.Empty(fixture.Policies.Created);
    }

    [PostgreSqlFact]
    public async Task Decision_ForANonPortPermissionNeverOpensAnything()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.CreateAsync(new PermissionRequest
        {
            Id = "tool-req", SessionId = SessionId, Owner = "alice", Tool = "Bash"
        });
        await database.Store.ResolveAsync("tool-req", "allow", SessionId);
        var fixture = Fixture(database.Store);
        var controller = fixture.Controller();

        Assert.Equal("deny", Json(await controller.PortRequestStatus(SessionId, "tool-req", CancellationToken.None))
            .GetProperty("decision").GetString());
        Assert.Empty(fixture.Policies.Created);
    }

    // ------------------------------------------------------------------ fixtures

    private static JsonElement Json(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
    }

    /// <summary>A PermissionStore pointed at an unreachable server: constructing it is fine,
    /// any query would throw — used to prove code paths that must not touch the store.</summary>
    private static PermissionStore DisconnectedPermissionStore()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1"
        }).Build();
        return new PermissionStore(cfg);
    }

    private static TestFixture Fixture(PermissionStore permissions, bool enabled = true, bool autoApprove = false)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Network:Enabled"] = enabled ? "true" : "false",
            ["Network:RequestablePorts"] = "5432, 3306, 9000-9100",
            ["AgentHub:Namespace"] = "test-sessions"
        }).Build();
        var grants = new InMemoryPortGrantStore();
        var policies = new RecordingPolicyClient();
        var service = new NetworkPortService(cfg, permissions, grants, policies, [], [],
            NullLogger<NetworkPortService>.Instance);
        var session = new SessionRecord
        {
            Id = SessionId, Owner = "alice", CallbackToken = Token,
            Mode = SessionMode.Interactive, Agent = AgentKind.Claude, AuthMode = AgentAuthMode.ApiKey,
            AutoApprove = autoApprove
        };
        return new TestFixture(service, grants, policies, session);
    }

    private sealed record TestFixture(
        NetworkPortService Service, InMemoryPortGrantStore Grants, RecordingPolicyClient Policies, SessionRecord Session)
    {
        public NetworkPortsController Controller(string? token = Token)
        {
            var controller = new NetworkPortsController(
                new AgentCallbackAuthorizer(new SingleSessionStore(Session)), Service)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            if (token is not null) controller.Request.Headers["X-Agent-Token"] = token;
            return controller;
        }
    }

    private sealed class InMemoryPortGrantStore : IPortGrantStore
    {
        private readonly List<PortGrant> _grants = [];
        public IReadOnlyList<PortGrant> All => _grants;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task AddAsync(string sessionId, PortDirection direction, int port, string protocol, CancellationToken ct = default)
        {
            if (!_grants.Any(g => g.SessionId == sessionId && g.Direction == direction && g.Port == port && g.Protocol == protocol))
                _grants.Add(new PortGrant(sessionId, direction, port, protocol, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string sessionId, PortDirection direction, int port, string protocol, CancellationToken ct = default) =>
            Task.FromResult(_grants.Any(g => g.SessionId == sessionId && g.Direction == direction && g.Port == port && g.Protocol == protocol));

        public Task<IReadOnlyList<PortGrant>> ListAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PortGrant>>(_grants.Where(g => g.SessionId == sessionId).ToList());

        public Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
        {
            _grants.RemoveAll(g => g.SessionId == sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPolicyClient : INetworkPolicyClient
    {
        public List<V1NetworkPolicy> Created { get; } = [];
        public List<(string Namespace, string SessionId)> DeletedSessions { get; } = [];

        public Task CreateAsync(IReadOnlyList<V1NetworkPolicy> policies, CancellationToken ct = default)
        {
            // Mirrors the Kubernetes client: an existing name is a no-op, not a duplicate.
            foreach (var policy in policies)
                if (Created.All(existing => existing.Metadata.Name != policy.Metadata.Name))
                    Created.Add(policy);
            return Task.CompletedTask;
        }

        public Task DeleteBySessionAsync(string namespaceName, string sessionId, CancellationToken ct = default)
        {
            DeletedSessions.Add((namespaceName, sessionId));
            return Task.CompletedTask;
        }
    }

    private sealed class SingleSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
