using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The account switch and status on every surface but the web app (docs/account-limits.md):
/// the remote REST routes the stdio MCP calls, the internal routes the in-pod MCP calls, and
/// the remote MCP tools — all over one fake service, so the status codes and error words are
/// what the tests pin, not the service.
/// </summary>
public sealed class AccountSwitchSurfaceTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ProviderAccountInfo Work = new("work0001", "Work", "w@example.com", null, Now, null, true, Now.AddHours(1), "mod five_hour", true);
    private static readonly ProviderAccountInfo Home = new("home0002", "Home", null, null, Now, null, false);

    private static SessionInfo Session(string id = "s1", string? credentialId = null, string? resolved = "work0001",
        AgentAuthMode auth = AgentAuthMode.Subscription, string? parent = null) => new()
    {
        Id = id, Title = "Coder", Owner = "alice", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "10.0.0.5",
        Agent = AgentKind.Claude, AuthMode = auth, CredentialId = credentialId, ResolvedCredentialId = resolved,
        ParentSessionId = parent, AccountFailover = "auto"
    };

    private static Dictionary<string, IReadOnlyList<ProviderAccountInfo>> Accounts() => new()
    {
        ["Claude"] = [Work, Home], ["Codex"] = [], ["Cursor"] = [], ["OpenClaw"] = []
    };

    // ------------------------------------------------------------------ the status itself

    [Fact]
    public void Status_NamesThePinnedElseResolvedElseDefaultAccount_AndListsTheRest()
    {
        var pinned = AccountStatus.From(Session(credentialId: "home0002"), Accounts());
        Assert.Equal("home0002", pinned.Account!.Id);
        Assert.False(pinned.Account.IsExhausted);
        Assert.Equal(["work0001"], pinned.Alternatives.Select(a => a.Id));

        var resolved = AccountStatus.From(Session(), Accounts());
        Assert.Equal("work0001", resolved.Account!.Id);
        Assert.True(resolved.Account.IsExhausted);
        Assert.Equal(Now.AddHours(1), resolved.Account.ExhaustedUntil);
        Assert.Equal(["home0002"], resolved.Alternatives.Select(a => a.Id));

        var fresh = AccountStatus.From(Session(resolved: null), Accounts());
        Assert.Equal("work0001", fresh.Account!.Id);

        var apiKey = AccountStatus.From(Session(auth: AgentAuthMode.ApiKey), Accounts());
        Assert.Null(apiKey.Account);
        Assert.Equal(2, apiKey.Alternatives.Count);
        Assert.Equal("auto", apiKey.AccountFailover);
    }

    // ------------------------------------------------------------------ remote REST

    [Fact]
    public async Task Remote_SwitchesForTheTokenOwner_AndMapsTheServiceFailures()
    {
        var svc = new Fake();
        var ok = await Remote(new RemoteCaller("alice", null), svc).SwitchCredential("s1", new("home0002"), CancellationToken.None);
        Assert.Equal("home0002", Assert.IsType<SessionInfo>(Assert.IsType<OkObjectResult>(ok.Result).Value).CredentialId);
        Assert.Equal(("alice", "s1", "home0002"), svc.Switched.Single());

        Assert.IsType<UnauthorizedResult>((await Remote(null, svc).SwitchCredential("s1", new("home0002"), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await Remote(new RemoteCaller("alice", null), svc).SwitchCredential("nope", new("home0002"), CancellationToken.None)).Result);

        foreach (var (exception, status) in new (Exception, int)[]
                 {
                     (new ArgumentException("bad"), 400), (new InvalidOperationException("stopped"), 409),
                     (new HttpRequestException("refused"), 502)
                 })
        {
            svc.SwitchThrows = exception;
            var result = (await Remote(new RemoteCaller("alice", null), svc).SwitchCredential("s1", new("home0002"), CancellationToken.None)).Result;
            Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        }
    }

    [Fact]
    public async Task Remote_ARestrictedTokenMayOnlySwitchToAnAccountItIsAllowed()
    {
        var svc = new Fake();
        var scope = new ApiTokenScope { ProviderAccounts = new Dictionary<string, List<string>> { ["Claude"] = ["work0001"] } };

        var refused = await Remote(new RemoteCaller("alice", scope), svc).SwitchCredential("s1", new("home0002"), CancellationToken.None);
        var allowed = await Remote(new RemoteCaller("alice", scope), svc).SwitchCredential("s1", new("work0001"), CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(refused.Result);
        Assert.Equal(403, forbidden.StatusCode);
        Assert.Equal(CredentialScopeException.CredentialNotAllowed, forbidden.Value);
        Assert.IsType<OkObjectResult>(allowed.Result);
        Assert.Equal("work0001", svc.Switched.Single().CredentialId);
    }

    [Fact]
    public async Task Remote_StatusIsNarrowedToTheTokensScope()
    {
        var svc = new Fake();
        var scope = new ApiTokenScope { ProviderAccounts = new Dictionary<string, List<string>> { ["Claude"] = ["work0001"] } };

        var full = await Remote(new RemoteCaller("alice", null), svc).AccountStatus("s1", CancellationToken.None);
        var narrowed = await Remote(new RemoteCaller("alice", scope), svc).AccountStatus("s1", CancellationToken.None);
        var missing = await Remote(new RemoteCaller("alice", null), svc).AccountStatus("nope", CancellationToken.None);

        Assert.Equal(["home0002"], Assert.IsType<AccountStatus>(Assert.IsType<OkObjectResult>(full.Result).Value).Alternatives.Select(a => a.Id));
        Assert.Empty(Assert.IsType<AccountStatus>(Assert.IsType<OkObjectResult>(narrowed.Result).Value).Alternatives);
        Assert.IsType<NotFoundResult>(missing.Result);
    }

    // ------------------------------------------------------------------ internal (in-pod)

    [Fact]
    public async Task Internal_ASessionSwitchesItself_OrADescendant_NeverAStranger()
    {
        var svc = new Fake();
        var controller = Internal(svc, "s1", token: "tok-s1");

        var self = await controller.SwitchCredential("s1", new("home0002"), CancellationToken.None);
        var child = await controller.SwitchPeerCredential("s1", "child", new("home0002"), CancellationToken.None);
        var stranger = await controller.SwitchPeerCredential("s1", "other", new("home0002"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(self);
        Assert.IsType<OkObjectResult>(child);
        Assert.IsType<NotFoundResult>(stranger);
        Assert.Equal(["s1", "child"], svc.Switched.Select(s => s.Id));
        Assert.IsType<UnauthorizedResult>(await Internal(svc, "s1", token: "wrong").SwitchCredential("s1", new("home0002"), CancellationToken.None));

        svc.SwitchThrows = new InvalidOperationException("stopped");
        Assert.IsType<ConflictObjectResult>(await controller.SwitchCredential("s1", new("home0002"), CancellationToken.None));
    }

    [Fact]
    public async Task Internal_StatusOfSelfAndDescendant()
    {
        var svc = new Fake();
        var controller = Internal(svc, "s1", token: "tok-s1");

        var self = Assert.IsType<AccountStatus>(Assert.IsType<OkObjectResult>(await controller.AccountStatus("s1", CancellationToken.None)).Value);
        var child = Assert.IsType<AccountStatus>(Assert.IsType<OkObjectResult>(await controller.PeerAccountStatus("s1", "child", CancellationToken.None)).Value);

        Assert.Equal("s1", self.SessionId);
        Assert.True(self.Account!.IsExhausted);
        Assert.Equal("child", child.SessionId);
        Assert.IsType<NotFoundResult>(await controller.PeerAccountStatus("s1", "other", CancellationToken.None));
    }

    // ------------------------------------------------------------------ remote MCP

    [Fact]
    public async Task McpTools_StatusAndSwitch_WithTheStableErrorWords()
    {
        var svc = new Fake();
        var tools = Tools(svc);

        var status = await tools.GetAccountStatus("s1");
        Assert.Equal("work0001", status.Account!.Id);
        Assert.Equal(["home0002"], status.Alternatives.Select(a => a.Id));
        Assert.Equal("session_not_found", (await Assert.ThrowsAsync<McpException>(() => tools.GetAccountStatus("nope"))).Message);

        var switched = await tools.SwitchAccount("s1", " home0002 ");
        Assert.Equal("home0002", switched.CredentialId);
        Assert.Equal("session_not_found", (await Assert.ThrowsAsync<McpException>(() => tools.SwitchAccount("nope", "home0002"))).Message);

        svc.SwitchThrows = new InvalidOperationException("The session is not running");
        Assert.StartsWith("session_not_running", (await Assert.ThrowsAsync<McpException>(() => tools.SwitchAccount("s1", "home0002"))).Message);
        svc.SwitchThrows = new ArgumentException("No stored Claude login with id 'x'.");
        Assert.StartsWith("invalid_argument", (await Assert.ThrowsAsync<McpException>(() => tools.SwitchAccount("s1", "x"))).Message);
        svc.SwitchThrows = new HttpRequestException("HTTP 409");
        Assert.StartsWith("pod_refused", (await Assert.ThrowsAsync<McpException>(() => tools.SwitchAccount("s1", "home0002"))).Message);
    }

    [Fact]
    public async Task McpTools_SessionCreate_PassesTheFailoverSetting()
    {
        var svc = new Fake();
        await Tools(svc).CreateSession(title: "x", accountFailover: "off");
        Assert.Equal("off", svc.LastCreate!.AccountFailover);
        await Tools(svc).CreateSession(title: "y");
        Assert.Null(svc.LastCreate!.AccountFailover);
    }

    // ------------------------------------------------------------------ fixtures

    private static RemoteController Remote(RemoteCaller? caller, ISessionService svc)
    {
        var controller = new RemoteController((_, _) => Task.FromResult(caller), svc)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = "Bearer oah_valid";
        return controller;
    }

    private static InternalController Internal(Fake svc, string sessionId, string token)
    {
        var controller = new InternalController(new TokenStore(svc.Records), [], svc, null!, [], [], null!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Headers["X-Agent-Token"] = token;
        return controller;
    }

    private static AgentHubMcpTools Tools(ISessionService svc)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"))
        };
        return new AgentHubMcpTools(new HttpContextAccessor { HttpContext = context }, svc, NullLogger<AgentHubMcpTools>.Instance);
    }

    private sealed class TokenStore(IReadOnlyList<SessionRecord> records) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default)
            => Task.FromResult(records.FirstOrDefault(r => r.Owner == owner && r.Id == id));
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default)
            => Task.FromResult(records.FirstOrDefault(r => r.CallbackToken == token));
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionRecord>>(records.Where(r => r.Owner == owner).ToList());
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Fake : ISessionService
    {
        public List<SessionInfo> Sessions { get; } =
        [
            Session(), Session("child", parent: "s1", resolved: "home0002"), Session("other")
        ];
        public List<SessionRecord> Records { get; } =
        [
            new() { Id = "s1", Owner = "alice", CallbackToken = "tok-s1", Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription },
            new() { Id = "child", Owner = "alice", CallbackToken = "tok-child", ParentSessionId = "s1" },
            new() { Id = "other", Owner = "alice", CallbackToken = "tok-other" }
        ];
        public Exception? SwitchThrows { get; set; }
        public List<(string Owner, string Id, string CredentialId)> Switched { get; } = [];
        public CreateSessionRequest? LastCreate { get; private set; }

        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default)
            => Task.FromResult(Sessions.FirstOrDefault(s => s.Owner == owner && s.Id == id));
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionInfo>>(Sessions.Where(s => s.Owner == owner).ToList());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>>(Accounts());
        public Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId, CancellationToken ct = default)
        {
            var session = Sessions.FirstOrDefault(s => s.Owner == owner && s.Id == id) ?? throw new KeyNotFoundException();
            if (SwitchThrows is not null) throw SwitchThrows;
            Switched.Add((owner, id, credentialId));
            return Task.FromResult(session with { CredentialId = credentialId });
        }
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        {
            LastCreate = req;
            return Task.FromResult(Session("new"));
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
