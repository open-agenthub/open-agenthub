using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Agents;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Agents;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class AllowedAgentsTests
{
    [Fact]
    public async Task CeProvider_AllowsAllKnownAgents()
    {
        var p = new AllowAllAgentsProvider();
        var allowed = await p.GetAllowedAsync(CancellationToken.None);
        Assert.Contains(AgentKind.OpenClaw, allowed);
        Assert.Equal(Enum.GetValues<AgentKind>().Length, allowed.Count);
        Assert.True(await p.IsAllowedAsync(AgentKind.Cursor));
        Assert.True(await p.IsAllowedAsync(AgentKind.Claude));
    }

    [Fact]
    public async Task EeProvider_WithoutConfig_AllowsAll_WhenLicensed()
    {
        var store = AllowedAgentsStore.InMemory();
        var p = new EeAllowedAgentsProvider(new FakeLicense(true), store);

        var allowed = await p.GetAllowedAsync();
        Assert.Equal(Enum.GetValues<AgentKind>().Length, allowed.Count);
        Assert.True(await p.IsAllowedAsync(AgentKind.OpenClaw));
    }

    [Fact]
    public async Task EeProvider_WithWhitelist_Restricts()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude, AgentKind.OpenClaw });
        var p = new EeAllowedAgentsProvider(new FakeLicense(true), store);

        var allowed = await p.GetAllowedAsync();
        Assert.Equal(2, allowed.Count);
        Assert.Contains(AgentKind.Claude, allowed);
        Assert.Contains(AgentKind.OpenClaw, allowed);
        Assert.True(await p.IsAllowedAsync(AgentKind.Claude));
        Assert.False(await p.IsAllowedAsync(AgentKind.Cursor));
        Assert.False(await p.IsAllowedAsync(AgentKind.Codex));
    }

    [Fact]
    public async Task EeProvider_Unlicensed_AllowsAll()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude });
        var p = new EeAllowedAgentsProvider(new FakeLicense(false), store);

        var allowed = await p.GetAllowedAsync();
        Assert.Equal(Enum.GetValues<AgentKind>().Length, allowed.Count);
        Assert.True(await p.IsAllowedAsync(AgentKind.Cursor));
    }

    [Fact]
    public async Task Store_EmptyReplace_ClearsRestriction()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude });
        Assert.False(await store.IsUnrestrictedAsync());

        await store.ReplaceAsync(Array.Empty<AgentKind>());
        Assert.True(await store.IsUnrestrictedAsync());
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task AdminGet_Returns402_WithoutLicense()
    {
        var controller = CreateController(licensed: false, admin: true, AllowedAgentsStore.InMemory());
        var response = await controller.Get(CancellationToken.None);
        var paymentRequired = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status402PaymentRequired, paymentRequired.StatusCode);
    }

    [Fact]
    public async Task AdminGet_ForbidsNonAdmin()
    {
        var controller = CreateController(licensed: true, admin: false, AllowedAgentsStore.InMemory());
        var response = await controller.Get(CancellationToken.None);
        Assert.IsType<ForbidResult>(response);
    }

    [Fact]
    public async Task AdminPutGet_RoundTripsWhitelist()
    {
        var store = AllowedAgentsStore.InMemory();
        var controller = CreateController(licensed: true, admin: true, store);

        var put = await controller.Put(
            new AllowedAgentsAdminController.SetAllowedAgentsReq(new[] { "Claude", "OpenClaw" }),
            CancellationToken.None);
        var putOk = Assert.IsType<OkObjectResult>(put);
        var putBody = Assert.IsType<AllowedAgentsAdminController.AllowedAgentsResponse>(putOk.Value);
        Assert.Equal(new[] { "Claude", "OpenClaw" }, putBody.Agents);

        var get = await controller.Get(CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(get);
        var getBody = Assert.IsType<AllowedAgentsAdminController.AllowedAgentsResponse>(getOk.Value);
        Assert.Equal(new[] { "Claude", "OpenClaw" }, getBody.Agents);

        var provider = new EeAllowedAgentsProvider(new FakeLicense(true), store);
        Assert.False(await provider.IsAllowedAsync(AgentKind.Cursor));
    }

    [Fact]
    public async Task AdminPut_Empty_ClearsRestriction()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude });
        var controller = CreateController(licensed: true, admin: true, store);

        var put = await controller.Put(
            new AllowedAgentsAdminController.SetAllowedAgentsReq(Array.Empty<string>()),
            CancellationToken.None);
        var putOk = Assert.IsType<OkObjectResult>(put);
        var body = Assert.IsType<AllowedAgentsAdminController.AllowedAgentsResponse>(putOk.Value);
        Assert.Empty(body.Agents);

        var provider = new EeAllowedAgentsProvider(new FakeLicense(true), store);
        Assert.True(await provider.IsAllowedAsync(AgentKind.Cursor));
    }

    [Fact]
    public async Task AdminPut_RejectsUnknownAgent()
    {
        var controller = CreateController(licensed: true, admin: true, AllowedAgentsStore.InMemory());
        var response = await controller.Put(
            new AllowedAgentsAdminController.SetAllowedAgentsReq(new[] { "NotAnAgent" }),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(response);
    }

    [Fact]
    public async Task EnsureAgentAllowed_RejectsOpenClaw_WhenWhitelistIsClaudeOnly()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude });
        var provider = new EeAllowedAgentsProvider(new FakeLicense(true), store);

        var ex = await Assert.ThrowsAsync<AgentNotAllowedException>(() =>
            AllowedAgentsGuard.EnsureAgentAllowedAsync(provider, AgentKind.OpenClaw));

        Assert.Contains("OpenClaw", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureAgentAllowed_Succeeds_WithCeAllowAll()
    {
        await AllowedAgentsGuard.EnsureAgentAllowedAsync(
            new AllowAllAgentsProvider(), AgentKind.OpenClaw);
        await AllowedAgentsGuard.EnsureAgentAllowedAsync(
            new AllowAllAgentsProvider(), AgentKind.Cursor);
    }

    [Fact]
    public async Task Create_Returns403_WhenOpenClawDisallowed()
    {
        var sessions = new ThrowingSessionService(
            () => throw new AgentNotAllowedException("Agent 'OpenClaw' is not allowed on this instance."));
        var controller = SessionsController(sessions);

        var response = await controller.Create(
            new CreateSessionRequest { Agent = AgentKind.OpenClaw, AuthMode = AgentAuthMode.Subscription },
            CancellationToken.None);

        AssertForbidden(response.Result, "OpenClaw");
    }

    [Fact]
    public async Task Resume_Returns403_WhenSessionAgentLaterDisallowed()
    {
        var sessions = new ThrowingSessionService(
            () => throw new AgentNotAllowedException("Agent 'OpenClaw' is not allowed on this instance."));
        var controller = SessionsController(sessions);

        var response = await controller.Resume("sess-1", CancellationToken.None);

        AssertForbidden(response.Result, "OpenClaw");
    }

    [Fact]
    public async Task Duplicate_Returns403_WhenTargetAgentDisallowed()
    {
        var sessions = new ThrowingSessionService(
            () => throw new AgentNotAllowedException("Agent 'Cursor' is not allowed on this instance."));
        var controller = SessionsController(sessions);

        var response = await controller.Duplicate("sess-1",
            new DuplicateSessionRequest("Copy", null, false, AgentKind.Cursor, AgentAuthMode.ApiKey),
            CancellationToken.None);

        AssertForbidden(response.Result, "Cursor");
    }

    [Fact]
    public async Task Update_Returns403_WhenAgentChangedToDisallowed()
    {
        var sessions = new ThrowingSessionService(
            () => throw new AgentNotAllowedException("Agent 'Cursor' is not allowed on this instance."));
        var controller = SessionsController(sessions);

        var response = await controller.Update("sess-1",
            new UpdateSessionRequest { Agent = AgentKind.Cursor, AuthMode = AgentAuthMode.ApiKey },
            CancellationToken.None);

        AssertForbidden(response.Result, "Cursor");
    }

    [Fact]
    public async Task Create_Succeeds_WithCeAllowAll()
    {
        var sessions = new AllowingSessionService();
        var controller = SessionsController(sessions);

        var response = await controller.Create(
            new CreateSessionRequest { Agent = AgentKind.OpenClaw, AuthMode = AgentAuthMode.Subscription },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task PublicGetAllowed_ReturnsCurrentList_ForAuthorizedUser()
    {
        var store = AllowedAgentsStore.InMemory();
        await store.ReplaceAsync(new[] { AgentKind.Claude, AgentKind.OpenClaw });
        var provider = new EeAllowedAgentsProvider(new FakeLicense(true), store);
        var controller = new AgentsController(provider)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("preferred_username", "alice") }, "test"))
                }
            }
        };

        var response = await controller.GetAllowed(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response);
        var body = Assert.IsType<AgentsController.AllowedAgentsResponse>(ok.Value);
        Assert.Equal(new[] { "Claude", "OpenClaw" }, body.Agents);
    }

    [Fact]
    public async Task PublicGetAllowed_ReturnsAll_WithCeAllowAll()
    {
        var controller = new AgentsController(new AllowAllAgentsProvider())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("preferred_username", "alice") }, "test"))
                }
            }
        };

        var response = await controller.GetAllowed(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response);
        var body = Assert.IsType<AgentsController.AllowedAgentsResponse>(ok.Value);
        Assert.Equal(Enum.GetValues<AgentKind>().Length, body.Agents.Count);
        Assert.Contains("OpenClaw", body.Agents);
    }

    private static void AssertForbidden(IActionResult? result, string agentName)
    {
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var message = Assert.IsType<string>(forbidden.Value);
        Assert.Contains(agentName, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not allowed", message, StringComparison.OrdinalIgnoreCase);
    }

    private static SessionsController SessionsController(ISessionService sessions)
    {
        var controller = new SessionsController(sessions, null!, Array.Empty<IPermissionPromptEditor>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("preferred_username", "alice") }, "test"))
                }
            }
        };
        return controller;
    }

    private static AllowedAgentsAdminController CreateController(
        bool licensed, bool admin, AllowedAgentsStore store)
    {
        var access = BuildAccess(admin ? "alice" : "bob");
        // Non-admin caller is "mallory"; admin caller is "alice".
        var owner = admin ? "alice" : "mallory";
        var controller = new AllowedAgentsAdminController(
            new FakeLicense(licensed), access, store)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("preferred_username", owner) }, "test"))
                }
            }
        };
        return controller;
    }

    private static AdminAccess BuildAccess(string admins)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ee:Admins"] = admins })
            .Build();
        return new AdminAccess(cfg, NullLogger<AdminAccess>.Instance, Array.Empty<IAdminRoleProvider>());
    }

    private sealed class FakeLicense(bool enabled) : IEnterpriseLicense
    {
        public LicenseStatus Status => enabled
            ? new LicenseStatus { Valid = true }
            : new LicenseStatus { Valid = false, Reason = "test" };
        public bool Enabled => enabled;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Session service that throws from every lifecycle mutation via <paramref name="onMutate"/>.</summary>
    private sealed class ThrowingSessionService(Func<Exception> onMutate) : ISessionService
    {
        private Exception Fail() => onMutate();

        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
            => Task.FromException<SessionInfo>(Fail());
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default)
            => Task.FromException<SessionInfo>(Fail());
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default)
            => Task.FromException<SessionInfo>(Fail());
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default)
            => Task.FromException<SessionInfo>(Fail());

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AllowingSessionService : ISessionService
    {
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
            => Task.FromResult(new SessionInfo
            {
                Id = "new", Title = req.Title, Owner = owner, Mode = req.Mode, Phase = "Pending",
                Agent = req.Agent, AuthMode = req.AuthMode
            });
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
