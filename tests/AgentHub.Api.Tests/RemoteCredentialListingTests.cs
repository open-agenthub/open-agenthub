using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// An API or MCP caller can list what it may pick a credential from, and name the login and
/// git tokens when it creates a session (docs/credential-scopes.md).
/// </summary>
public sealed class RemoteCredentialListingTests
{
    private static readonly ProviderAccountInfo Work = new("work0001", "Work", "me@example.com", "Example Org", DateTime.UtcNow, null, true);

    [Fact]
    public async Task RemoteListing_AnswersAccountsPatsAndApiKeyPresence_ForTheTokenOwner()
    {
        var svc = new FakeSessions();
        var controller = Remote(owner: "alice", svc, "oah_valid");

        var result = await controller.Credentials(CancellationToken.None);

        var listing = Assert.IsType<RemoteCredentialListing>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("alice", svc.ListedOwner);
        Assert.Equal("Work", Assert.Single(listing.Accounts["Claude"]).Label);
        Assert.Equal("gitlab.example.com", Assert.Single(listing.GitPats).Host);
        Assert.True(listing.ApiKeys.Anthropic);
        Assert.False(listing.ApiKeys.OpenAi);
        // The wire form uses the agent names of the in-app listing and the provider names
        // callers already know; nothing in it is a token.
        var json = JsonSerializer.Serialize(listing, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"Claude\"", json);
        Assert.Contains("\"openai\":false", json);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteListing_WithoutAValidToken_IsUnauthorizedAndReadsNothing()
    {
        var svc = new FakeSessions();
        var controller = Remote(owner: null, svc, "oah_unknown");

        Assert.IsType<UnauthorizedResult>((await controller.Credentials(CancellationToken.None)).Result);
        Assert.Null(svc.ListedOwner);
    }

    [Fact]
    public async Task McpCredentialsList_AnswersTheSameListingForTheSignedInUser()
    {
        var svc = new FakeSessions();

        var listing = await Tools(svc).ListCredentials();

        Assert.Equal("alice", svc.ListedOwner);
        Assert.Equal("work0001", Assert.Single(listing.Accounts["Claude"]).Id);
        Assert.Single(listing.GitPats);
    }

    [Fact]
    public async Task McpSessionCreate_PassesTheLoginAndTheGitTokenIdsThrough()
    {
        var svc = new FakeSessions();
        var tools = Tools(svc);

        await tools.CreateSession(title: "t", credentialId: " work0001 ", gitPatIds: "p1, p2,,p1");
        Assert.Equal("work0001", svc.LastRequest!.CredentialId);
        Assert.Equal(["p1", "p2"], svc.LastRequest.GitPatIds);

        // Unspecified leaves the service's defaults alone: the default account, every token.
        await tools.CreateSession(title: "t", credentialId: "", gitPatIds: "*");
        Assert.Null(svc.LastRequest!.CredentialId);
        Assert.Null(svc.LastRequest.GitPatIds);

        await tools.CreateSession(title: "t", gitPatIds: "none");
        Assert.Empty(svc.LastRequest!.GitPatIds!);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" * ", null)]
    [InlineData("NONE", "")]
    [InlineData("a,b", "a,b")]
    [InlineData(" a , , b , a ", "a,b")]
    public void ParseIdList_ReadsTheTextForm(string? input, string? expected)
    {
        var parsed = AgentHubMcpTools.ParseIdList(input);
        Assert.Equal(expected, parsed is null ? null : string.Join(',', parsed));
    }

    // ------------------------------------------------------------------ fixtures

    private static RemoteController Remote(string? owner, FakeSessions svc, string bearer)
    {
        var controller = new RemoteController((_, _) => Task.FromResult(owner), svc)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = $"Bearer {bearer}";
        return controller;
    }

    private static AgentHubMcpTools Tools(FakeSessions svc)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"))
        };
        return new AgentHubMcpTools(new HttpContextAccessor { HttpContext = context }, svc, NullLogger<AgentHubMcpTools>.Instance);
    }

    private sealed class FakeSessions : ISessionService
    {
        public string? ListedOwner { get; private set; }
        public CreateSessionRequest? LastRequest { get; private set; }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(string owner, CancellationToken ct = default)
        {
            ListedOwner = owner;
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>>(
                new Dictionary<string, IReadOnlyList<ProviderAccountInfo>> { ["Claude"] = [Work], ["Codex"] = [] });
        }

        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult(new CredentialStatus
            {
                AnthropicApiKey = true,
                GitPats = [new GitPatInfo("p1", "gitlab", "gitlab.example.com")]
            });

        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        {
            LastRequest = req;
            return Task.FromResult(new SessionInfo { Id = "s1", Title = req.Title, Owner = owner, Mode = req.Mode, Phase = "Pending" });
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
