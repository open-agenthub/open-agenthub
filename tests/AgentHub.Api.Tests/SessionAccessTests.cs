using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionAccessTests
{
    private static SessionRecord Session(string owner = "owner-1") => new()
    {
        Id = "session-1",
        Owner = owner,
        Title = "Shared session",
        Mode = SessionMode.Interactive,
        ClaudeSessionId = "claude-session",
        CallbackToken = "callback-token"
    };

    [Theory]
    [InlineData(true, null, SessionAccessLevel.Owner)]
    [InlineData(false, ShareRole.Collaborator, SessionAccessLevel.Collaborator)]
    [InlineData(false, ShareRole.Viewer, SessionAccessLevel.Viewer)]
    [InlineData(false, null, SessionAccessLevel.None)]
    public void EffectiveAccess_OwnerWins(bool owns, ShareRole? role, SessionAccessLevel expected)
        => Assert.Equal(expected, SessionAccessRules.Resolve(owns, role));

    [Theory]
    [InlineData(SessionAccessLevel.None, false)]
    [InlineData(SessionAccessLevel.Viewer, false)]
    [InlineData(SessionAccessLevel.Collaborator, true)]
    [InlineData(SessionAccessLevel.Owner, true)]
    public void TerminalWrite_RequiresCollaboratorOrOwner(SessionAccessLevel level, bool expected)
        => Assert.Equal(expected, SessionAccessRules.CanWriteTerminal(level));

    [Fact]
    public void SharedDto_ExposesOnlyDisplaySafeSessionData()
    {
        var session = new SessionRecord
        {
            Id = "session-1",
            Owner = "owner-1",
            Title = "Shared session",
            Mode = SessionMode.Interactive,
            RepoUrl = "https://example.test/legacy.git",
            ReposJson = """[{"url":"https://example.test/repo.git","branch":"secret-branch","providerId":"credential-id"}]""",
            ClaudeSessionId = "claude-secret",
            CallbackToken = "callback-secret",
            McpConfigJson = """{"mcpServers":{"private":{"env":{"TOKEN":"secret"}}}}""",
            Image = "private.registry/agent:latest",
            RunAsRoot = true,
            Cpu = "8",
            Memory = "16Gi",
            Status = "Running"
        };

        var dto = SharedSessionSanitizer.Sanitize(
            new SessionAccessResult(session, SessionAccessLevel.Viewer, session.Owner));

        Assert.Equal("Viewer", dto.AccessRole);
        Assert.Equal("owner-1", dto.SharedBy);
        Assert.False(dto.CanManage);
        Assert.False(dto.CanWrite);
        Assert.False(dto.CanShell);
        Assert.Null(dto.McpConfigJson);
        Assert.Equal(
            ["https://example.test/repo.git", "https://example.test/legacy.git"],
            dto.RepositoryUrls);

        var properties = dto.GetType().GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Image", properties);
        Assert.DoesNotContain("RunAsRoot", properties);
        Assert.DoesNotContain("Cpu", properties);
        Assert.DoesNotContain("Memory", properties);
        Assert.DoesNotContain("Repos", properties);
        Assert.DoesNotContain("CallbackToken", properties);
        Assert.DoesNotContain("ClaudeSessionId", properties);
    }

    [Fact]
    public async Task ResolveUserAsync_OwnerWinsOverStoredGrant()
    {
        var source = new FakeAccessStore
        {
            UserAccess = new StoredSessionAccess(Session("owner-1"), ShareRole.Viewer)
        };
        var service = new SessionAccessService(source);

        var result = await service.ResolveUserAsync("owner-1", "session-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(SessionAccessLevel.Owner, result.Level);
        Assert.Null(result.SharedBy);
    }

    [Fact]
    public async Task ResolveUserAsync_UsesDirectGrantAndIdentifiesOwner()
    {
        var source = new FakeAccessStore
        {
            UserAccess = new StoredSessionAccess(Session(), ShareRole.Collaborator)
        };
        var service = new SessionAccessService(source);

        var result = await service.ResolveUserAsync("recipient-1", "session-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(SessionAccessLevel.Collaborator, result.Level);
        Assert.Equal("owner-1", result.SharedBy);
    }

    [Fact]
    public async Task ResolveTokenAsync_UsesLinkRoleAndDoesNotRevealFailureReason()
    {
        var source = new FakeAccessStore
        {
            TokenAccess = new StoredSessionAccess(Session(), ShareRole.Viewer)
        };
        var service = new SessionAccessService(source);

        var allowed = await service.ResolveTokenAsync("valid-token", CancellationToken.None);
        source.TokenAccess = null;
        var denied = await service.ResolveTokenAsync("invalid-or-expired-token", CancellationToken.None);

        Assert.NotNull(allowed);
        Assert.Equal(SessionAccessLevel.Viewer, allowed.Level);
        Assert.Equal("owner-1", allowed.SharedBy);
        Assert.Null(denied);
    }

    [Fact]
    public async Task OwnerSharingApi_RequiresEnabledEnterpriseLicenseBeforeStoreAccess()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] =
                    "Host=127.0.0.1;Database=unused;Username=unused;Password=unused;Timeout=1"
            })
            .Build();
        var store = new SessionShareStore(configuration, NullLogger<SessionShareStore>.Instance);
        var controller = new SharingController(store, new FakeLicense(enabled: false));

        var response = await controller.List("session-1", CancellationToken.None);

        var paymentRequired = Assert.IsType<ObjectResult>(response);
        Assert.Equal(402, paymentRequired.StatusCode);
    }

    [Fact]
    public async Task SecretLinkSessionApi_ReturnsSanitizedDto()
    {
        var session = Session();
        session.McpConfigJson = """{"mcpServers":{"private":{}}}""";
        session.Image = "private.registry/agent:latest";
        var access = new FakeAccessService
        {
            TokenResult = new SessionAccessResult(session, SessionAccessLevel.Viewer, session.Owner)
        };
        var controller = new SharedSessionsController(access);

        var response = await controller.GetSession("secret-token", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<SharedSessionInfo>(ok.Value);
        Assert.Equal("Viewer", dto.AccessRole);
        Assert.Null(dto.McpConfigJson);
        Assert.False(dto.CanManage);
        Assert.DoesNotContain("Image", dto.GetType().GetProperties().Select(p => p.Name));
    }

    private sealed class FakeAccessStore : ISessionAccessStore
    {
        public StoredSessionAccess? UserAccess { get; set; }
        public StoredSessionAccess? TokenAccess { get; set; }

        public Task<StoredSessionAccess?> FindUserAccessAsync(
            string principal, string sessionId, CancellationToken ct = default)
            => Task.FromResult(UserAccess);

        public Task<StoredSessionAccess?> FindTokenAccessAsync(
            string token, CancellationToken ct = default)
            => Task.FromResult(TokenAccess);
    }

    private sealed class FakeAccessService : ISessionAccessService
    {
        public SessionAccessResult? TokenResult { get; init; }

        public Task<SessionAccessResult?> ResolveUserAsync(
            string principal, string sessionId, CancellationToken ct = default)
            => Task.FromResult<SessionAccessResult?>(null);

        public Task<SessionAccessResult?> ResolveTokenAsync(
            string token, CancellationToken ct = default)
            => Task.FromResult(TokenResult);
    }

    private sealed class FakeLicense(bool enabled) : IEnterpriseLicense
    {
        public LicenseStatus Status { get; } = new() { Valid = enabled };
        public bool Enabled => enabled;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
