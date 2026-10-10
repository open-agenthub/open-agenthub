using System.Security.Claims;
using System.Text;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The HTTP edges of provider accounts: the pod writeback that now targets an account and
/// re-points the session, and the user-facing account endpoints.
/// </summary>
public sealed class ProviderAccountEndpointsTests
{
    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ------------------------------------------------------------------ writeback from the pod

    [Fact]
    public async Task Writeback_PassesTheMountedAccountAndTheHeaderIdentityToTheService()
    {
        var service = new RecordingSessionService { ReturnAccountId = "mounted1" };
        var store = new CallbackSessionStore(Session(credentialId: "mounted1"));
        var controller = Internal(store, service, "{\"claudeAiOauth\":{\"accessToken\":\"t\"}}",
            identityHeader: Base64Url("{\"key\":\"uuid:org\",\"email\":\"me@example.com\"}"));

        var result = await controller.ProviderCredentials("session-1", "claude", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("mounted1", service.MountedCredentialId);
        Assert.Equal("uuid:org", service.Identity!.Key);
        Assert.Equal("me@example.com", service.Identity.Email);
        // Same account as before: nothing to re-point.
        Assert.Empty(store.CredentialUpdates);
    }

    [Fact]
    public async Task Writeback_RePointsTheSessionWhenTheServiceAttachedAnotherAccount()
    {
        var service = new RecordingSessionService { ReturnAccountId = "fresh002" };
        var store = new CallbackSessionStore(Session(credentialId: null, agent: AgentKind.Codex));
        var controller = Internal(store, service, "{\"tokens\":{\"access_token\":\"t\"}}", agent: AgentKind.Codex);

        var result = await controller.ProviderCredentials("session-1", "codex", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(service.MountedCredentialId);
        Assert.Equal([("session-1", "fresh002")], store.CredentialUpdates);
    }

    [Fact]
    public async Task Writeback_ExtractsIdentityFromTheFileWhenNoHeaderIsSent()
    {
        var service = new RecordingSessionService { ReturnAccountId = "x" };
        var token = "h." + Base64Url("{\"email\":\"dev@example.com\"}") + ".s";
        var controller = Internal(new CallbackSessionStore(Session(credentialId: "x", agent: AgentKind.Codex)), service,
            $"{{\"tokens\":{{\"id_token\":\"{token}\"}}}}", agent: AgentKind.Codex);

        await controller.ProviderCredentials("session-1", "codex", CancellationToken.None);

        Assert.Equal("dev@example.com", service.Identity!.Email);
    }

    [Fact]
    public async Task Writeback_AGarbledIdentityHeaderDoesNotRejectTheCredential()
    {
        var service = new RecordingSessionService { ReturnAccountId = "x" };
        var controller = Internal(new CallbackSessionStore(Session(credentialId: "x")), service,
            "{\"claudeAiOauth\":{\"accessToken\":\"t\"}}", identityHeader: "%%% not base64 %%%");

        var result = await controller.ProviderCredentials("session-1", "claude", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(service.Identity);
        Assert.Equal(1, service.StoreCalls);
    }

    // ------------------------------------------------------------------ account endpoints

    [Fact]
    public async Task Accounts_ListsTheCallersAccountsByAgent()
    {
        var service = new RecordingSessionService
        {
            Accounts = new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>
            {
                ["Claude"] = [new("a1", "Work", "w@example.com", "Org", DateTime.UtcNow, null, true)]
            }
        };

        var result = await Credentials(service, "alice").Accounts(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var listing = Assert.IsAssignableFrom<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>>(ok.Value);
        Assert.Equal("Work", Assert.Single(listing["Claude"]).Label);
        Assert.Equal("alice", service.ListedOwner);
    }

    [Fact]
    public async Task UpdateAccount_RenamesAndMakesDefault_ForTheCallerOnly()
    {
        var service = new RecordingSessionService
        {
            Updated = new("a1", "Personal", null, null, DateTime.UtcNow, null, true)
        };

        var result = await Credentials(service, "alice").UpdateAccount(AgentKind.Claude, "a1",
            new UpdateProviderAccountRequest("Personal", true), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Personal", Assert.IsType<ProviderAccountInfo>(ok.Value).Label);
        Assert.Equal(("alice", AgentKind.Claude, "a1"), service.UpdatedTarget);
    }

    /// <summary>The manual way out of a wrong limit detection (docs/account-limits.md) rides on
    /// the same PATCH; the controller forwards the flag untouched.</summary>
    [Fact]
    public async Task UpdateAccount_ForwardsClearExhausted()
    {
        var service = new RecordingSessionService { Updated = new("a1", "Work", null, null, DateTime.UtcNow, null, true) };

        var result = await Credentials(service, "alice").UpdateAccount(AgentKind.Codex, "a1",
            new UpdateProviderAccountRequest(ClearExhausted: true), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(service.UpdatedRequest!.ClearExhausted);
        Assert.Null(service.UpdatedRequest.Label);
    }

    [Fact]
    public async Task UpdateAccount_UnknownAccountIs404_AndBadIdOrLabelIs400()
    {
        var service = new RecordingSessionService { Updated = null };
        var controller = Credentials(service, "alice");

        Assert.IsType<NotFoundResult>((await controller.UpdateAccount(AgentKind.Codex, "nope",
            new UpdateProviderAccountRequest("x"), CancellationToken.None)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.UpdateAccount(AgentKind.Codex, "../x",
            new UpdateProviderAccountRequest("x"), CancellationToken.None)).Result);

        service.UpdateException = new ArgumentException("A label is required.");
        Assert.IsType<BadRequestObjectResult>((await controller.UpdateAccount(AgentKind.Codex, "a1",
            new UpdateProviderAccountRequest(" "), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task DeleteAccount_IsIdempotentAndScopedToTheCaller()
    {
        var service = new RecordingSessionService();

        var first = await Credentials(service, "alice").DeleteAccount(AgentKind.Cursor, "a1", CancellationToken.None);
        var second = await Credentials(service, "bob").DeleteAccount(AgentKind.Cursor, "a1", CancellationToken.None);
        var bad = await Credentials(service, "bob").DeleteAccount(AgentKind.Cursor, "A1", CancellationToken.None);

        Assert.IsType<NoContentResult>(first);
        Assert.IsType<NoContentResult>(second);
        Assert.IsType<BadRequestObjectResult>(bad);
        Assert.Equal([("alice", AgentKind.Cursor, "a1"), ("bob", AgentKind.Cursor, "a1")], service.DeletedAccounts);
    }

    // ------------------------------------------------------------------ fixtures

    private static SessionRecord Session(string? credentialId, AgentKind agent = AgentKind.Claude) => new()
    {
        Id = "session-1", Owner = "alice", CallbackToken = "callback-token",
        Agent = agent, AuthMode = AgentAuthMode.Subscription, CredentialId = credentialId
    };

    private static InternalController Internal(CallbackSessionStore store, RecordingSessionService service, string body,
        AgentKind agent = AgentKind.Claude, string? identityHeader = null)
    {
        var controller = new InternalController(store, [], service, null!, [], [], null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Headers["X-Agent-Token"] = "callback-token";
        if (identityHeader is not null) controller.Request.Headers[ProviderAccountIdentityReader.HeaderName] = identityHeader;
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return controller;
    }

    private static CredentialsController Credentials(RecordingSessionService service, string user)
    {
        var controller = new CredentialsController(service);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test"))
            }
        };
        return controller;
    }

    private sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
    {
        public List<(string Id, string? CredentialId)> CredentialUpdates { get; } = new();
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default)
            => Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCredentialIdAsync(string id, string? credentialId, CancellationToken ct = default)
        {
            CredentialUpdates.Add((id, credentialId));
            return Task.CompletedTask;
        }
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public int StoreCalls { get; private set; }
        public string? ReturnAccountId { get; init; }
        public string? MountedCredentialId { get; private set; }
        public ProviderAccountIdentity? Identity { get; private set; }
        public string? ListedOwner { get; private set; }
        public IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> Accounts { get; init; } =
            new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>();
        public ProviderAccountInfo? Updated { get; init; }
        public Exception? UpdateException { get; set; }
        public (string Owner, AgentKind Agent, string Id)? UpdatedTarget { get; private set; }
        public UpdateProviderAccountRequest? UpdatedRequest { get; private set; }
        public List<(string Owner, AgentKind Agent, string Id)> DeletedAccounts { get; } = new();

        public Task<string?> StoreProviderLoginAsync(string owner, AgentKind agent, string json,
            ProviderAccountIdentity? identity, string? mountedCredentialId, CancellationToken ct = default)
        {
            StoreCalls++;
            MountedCredentialId = mountedCredentialId;
            Identity = identity;
            return Task.FromResult(ReturnAccountId);
        }
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(string owner, CancellationToken ct = default)
        {
            ListedOwner = owner;
            return Task.FromResult(Accounts);
        }
        public Task<ProviderAccountInfo?> UpdateProviderAccountAsync(string owner, AgentKind agent, string id, UpdateProviderAccountRequest req, CancellationToken ct = default)
        {
            if (UpdateException is not null) throw UpdateException;
            UpdatedTarget = (owner, agent, id);
            UpdatedRequest = req;
            return Task.FromResult(Updated);
        }
        public Task DeleteProviderAccountAsync(string owner, AgentKind agent, string id, CancellationToken ct = default)
        {
            DeletedAccounts.Add((owner, agent, id));
            return Task.CompletedTask;
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => Task.FromResult(new CredentialStatus());
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
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
