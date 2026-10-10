using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The matrix of docs/credential-scopes.md: what a restricted token may bring into existence, and
/// how the remote surface answers when it may not.
/// </summary>
public sealed class RemoteCredentialScopeTests
{
    private static readonly ProviderAccountInfo Work = new("work0001", "Work", "w@example.com", null, DateTime.UtcNow, null, true);
    private static readonly ProviderAccountInfo Home = new("home0002", "Home", "h@example.com", null, DateTime.UtcNow, null, false);
    private static readonly ProviderAccountInfo Ci = new("ci000003", "CI", null, null, DateTime.UtcNow, null, false);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> Accounts =
        new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>
        {
            ["Claude"] = [Work, Home, Ci],
            ["Codex"] = [],
            ["Cursor"] = [],
            ["OpenClaw"] = []
        };

    private static readonly IReadOnlyList<GitPatInfo> Pats =
    [
        new("pat-a", "gitlab", "gitlab.example.com"),
        new("pat-b", "github", "github.com")
    ];

    private static CreateSessionRequest Request(string? credentialId = null, List<string>? gitPatIds = null,
        AgentAuthMode auth = AgentAuthMode.Subscription, AgentKind agent = AgentKind.Claude) => new()
    {
        Title = "t", Agent = agent, AuthMode = auth, CredentialId = credentialId, GitPatIds = gitPatIds
    };

    private static ApiTokenScope Scope(string[]? claude = null, string[]? git = null, bool? apiKeys = null) => new()
    {
        ProviderAccounts = claude is null ? null : new Dictionary<string, List<string>> { ["Claude"] = claude.ToList() },
        GitPats = git?.ToList(),
        ApiKeys = apiKeys
    };

    private static string Code(Func<CreateSessionRequest> act) =>
        Assert.Throws<CredentialScopeException>(act).Code;

    // ------------------------------------------------------------------ provider accounts

    [Fact]
    public void NoScope_LeavesTheRequestAlone()
    {
        var req = Request("home0002", ["pat-a"]);
        Assert.Same(req, CredentialScope.ApplyToCreate(req, null, Accounts, Pats));
    }

    [Fact]
    public void ANamedAccountOutsideTheScope_IsRefused_AndInsideItPasses()
    {
        var scope = Scope(claude: ["ci000003"], git: ["*"]);
        Assert.Equal("credential_not_allowed", Code(() => CredentialScope.ApplyToCreate(Request("home0002"), scope, Accounts, Pats)));
        Assert.Equal("ci000003", CredentialScope.ApplyToCreate(Request("ci000003"), scope, Accounts, Pats).CredentialId);
    }

    [Fact]
    public void WithoutAnAccount_TheDefaultIsKeptWhenAllowed()
    {
        var scope = Scope(claude: ["work0001", "ci000003"], git: ["*"]);
        // The default (Work) is allowed, so the session keeps following the default: null stays null.
        Assert.Null(CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats).CredentialId);
    }

    [Fact]
    public void WithoutAnAccount_TheSingleAllowedOneIsPinned()
    {
        var scope = Scope(claude: ["ci000003"], git: ["*"]);
        Assert.Equal("ci000003", CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats).CredentialId);
    }

    [Fact]
    public void WithoutAnAccount_TwoAllowedNonDefaultsAreARefusalNotAGuess()
    {
        var scope = Scope(claude: ["home0002", "ci000003"], git: ["*"]);
        Assert.Equal("credential_required", Code(() => CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats)));
    }

    [Fact]
    public void AnAllowedIdThatNoLongerExists_DoesNotCountAsAChoice()
    {
        // The one remaining allowed account was removed since the scope was written.
        var scope = Scope(claude: ["gone0000"], git: ["*"]);
        Assert.Equal("credential_required", Code(() => CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats)));
        Assert.Equal("credential_not_allowed", Code(() => CredentialScope.ApplyToCreate(Request("work0001"), scope, Accounts, Pats)));
    }

    [Fact]
    public void TheWildcard_AllowsAnyAccountAndKeepsTheDefault_EvenWithNoneStored()
    {
        var scope = new ApiTokenScope
        {
            ProviderAccounts = new() { ["claude"] = ["*"], ["codex"] = ["*"] }, GitPats = ["*"]
        };
        Assert.Null(CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats).CredentialId);
        Assert.Equal("home0002", CredentialScope.ApplyToCreate(Request("home0002"), scope, Accounts, Pats).CredentialId);
        // Codex has no stored login: the session starts and the person signs in inside it, as without a scope.
        Assert.Null(CredentialScope.ApplyToCreate(Request(agent: AgentKind.Codex), scope, Accounts, Pats).CredentialId);
    }

    [Fact]
    public void AnAgentTheScopeDoesNotName_IsNotAllowed()
    {
        var scope = Scope(claude: ["*"], git: ["*"]);
        Assert.Equal("agent_not_allowed", Code(() => CredentialScope.ApplyToCreate(Request(agent: AgentKind.Codex), scope, Accounts, Pats)));
        // An empty list names the agent but allows no account of it: nothing to pin, so refused too.
        var emptyClaude = Scope(claude: [], git: ["*"]);
        Assert.Equal("credential_required", Code(() => CredentialScope.ApplyToCreate(Request(), emptyClaude, Accounts, Pats)));
    }

    [Fact]
    public void ApiKeySessions_NeedTheApiKeysFlag_AndIgnoreTheAccountList()
    {
        Assert.Equal("api_keys_not_allowed", Code(() =>
            CredentialScope.ApplyToCreate(Request(auth: AgentAuthMode.ApiKey), Scope(claude: ["*"], git: ["*"]), Accounts, Pats)));
        var allowed = CredentialScope.ApplyToCreate(Request(auth: AgentAuthMode.ApiKey, agent: AgentKind.Codex),
            Scope(git: ["*"], apiKeys: true), Accounts, Pats);
        Assert.Equal(AgentAuthMode.ApiKey, allowed.AuthMode);
        Assert.Null(allowed.CredentialId);
    }

    // ------------------------------------------------------------------ git PATs

    [Fact]
    public void Git_ARequestThatSaysNothing_GetsTheAllowedStoredTokens()
    {
        var scope = Scope(claude: ["*"], git: ["pat-b", "gone-pat"]);
        Assert.Equal(["pat-b"], CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats).GitPatIds);
        // "all" spelled out by the caller means the same thing for a restricted token.
        Assert.Equal(["pat-b"], CredentialScope.ApplyToCreate(Request(gitPatIds: ["*"]), scope, Accounts, Pats).GitPatIds);
    }

    [Fact]
    public void Git_NoPatsInTheScope_MeansNoPats()
    {
        var scope = Scope(claude: ["*"]);
        Assert.Empty(CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats).GitPatIds!);
        Assert.Equal("git_pat_not_allowed", Code(() => CredentialScope.ApplyToCreate(Request(gitPatIds: ["pat-a"]), scope, Accounts, Pats)));
        // Asking for none is always fine.
        Assert.Empty(CredentialScope.ApplyToCreate(Request(gitPatIds: []), scope, Accounts, Pats).GitPatIds!);
    }

    [Fact]
    public void Git_ANamedTokenOutsideTheScope_IsRefused_AndInsideItPasses()
    {
        var scope = Scope(claude: ["*"], git: ["pat-b"]);
        Assert.Equal("git_pat_not_allowed", Code(() => CredentialScope.ApplyToCreate(Request(gitPatIds: ["pat-a", "pat-b"]), scope, Accounts, Pats)));
        Assert.Equal(["pat-b"], CredentialScope.ApplyToCreate(Request(gitPatIds: [" pat-b "]), scope, Accounts, Pats).GitPatIds);
    }

    [Fact]
    public void Git_TheWildcard_LeavesTheSelectionToTheCaller_SoLaterTokensStillArrive()
    {
        var scope = Scope(claude: ["*"], git: ["*"]);
        Assert.Null(CredentialScope.ApplyToCreate(Request(), scope, Accounts, Pats).GitPatIds);
        Assert.Equal(["pat-a"], CredentialScope.ApplyToCreate(Request(gitPatIds: ["pat-a"]), scope, Accounts, Pats).GitPatIds);
    }

    // ------------------------------------------------------------------ listing

    [Fact]
    public void Listing_ShowsOnlyWhatTheTokenMayUse()
    {
        var listing = RemoteCredentialListing.From(Accounts, new CredentialStatus
        {
            GitPats = Pats, AnthropicApiKey = true, OpenAiApiKey = true
        });

        var narrowed = CredentialScope.FilterListing(listing, Scope(claude: ["home0002"], git: ["pat-a"]));

        Assert.Equal(["home0002"], narrowed.Accounts["Claude"].Select(a => a.Id));
        Assert.Empty(narrowed.Accounts["Codex"]);
        Assert.Equal(["pat-a"], narrowed.GitPats.Select(p => p.Id));
        Assert.False(narrowed.ApiKeys.Anthropic);
        Assert.False(narrowed.ApiKeys.OpenAi);

        var wide = CredentialScope.FilterListing(listing, new ApiTokenScope
        {
            ProviderAccounts = new() { ["Claude"] = ["*"] }, GitPats = ["*"], ApiKeys = true
        });
        Assert.Equal(3, wide.Accounts["Claude"].Count);
        Assert.Equal(2, wide.GitPats.Count);
        Assert.True(wide.ApiKeys.Anthropic);
        Assert.Same(listing, CredentialScope.FilterListing(listing, null));
    }

    // ------------------------------------------------------------------ the remote surface

    [Fact]
    public async Task RemoteCreate_AnswersTheCodeAs403_AndNeverReachesTheService()
    {
        var svc = new ScopedSessions();
        var controller = Remote(new RemoteCaller("alice", Scope(claude: ["ci000003"])), svc);

        var result = await controller.Create(Request("home0002"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        Assert.Equal("credential_not_allowed", status.Value);
        Assert.Null(svc.Created);
    }

    [Fact]
    public async Task RemoteCreate_PassesTheNarrowedRequestToTheService()
    {
        var svc = new ScopedSessions();
        var controller = Remote(new RemoteCaller("alice", Scope(claude: ["ci000003"], git: ["pat-b"])), svc);

        var result = await controller.Create(Request(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("ci000003", svc.Created!.CredentialId);
        Assert.Equal(["pat-b"], svc.Created.GitPatIds);
        Assert.Equal("alice", svc.CreatedFor);
    }

    [Fact]
    public async Task RemoteCreate_WithoutAScope_SkipsTheCredentialLookupsEntirely()
    {
        var svc = new ScopedSessions { Lookups = false };
        var controller = Remote(new RemoteCaller("alice", null), svc);

        Assert.IsType<OkObjectResult>((await controller.Create(Request("home0002", ["pat-a"]), CancellationToken.None)).Result);
        Assert.Equal("home0002", svc.Created!.CredentialId);
    }

    [Fact]
    public async Task RemoteListing_IsNarrowedToTheScope()
    {
        var controller = Remote(new RemoteCaller("alice", Scope(claude: ["home0002"], git: ["pat-b"], apiKeys: true)), new ScopedSessions());

        var result = await controller.Credentials(CancellationToken.None);

        var listing = Assert.IsType<RemoteCredentialListing>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["home0002"], listing.Accounts["Claude"].Select(a => a.Id));
        Assert.Equal(["pat-b"], listing.GitPats.Select(p => p.Id));
        Assert.True(listing.ApiKeys.Anthropic);
    }

    private static RemoteController Remote(RemoteCaller caller, ISessionService svc)
    {
        var controller = new RemoteController((_, _) => Task.FromResult<RemoteCaller?>(caller), svc)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = "Bearer oah_valid";
        return controller;
    }

    private sealed class ScopedSessions : ISessionService
    {
        public bool Lookups { get; init; } = true;
        public CreateSessionRequest? Created { get; private set; }
        public string? CreatedFor { get; private set; }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(string owner, CancellationToken ct = default)
            => Lookups ? Task.FromResult(Accounts) : throw new InvalidOperationException("no lookup expected");

        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default)
            => Lookups
                ? Task.FromResult(new CredentialStatus { GitPats = Pats, AnthropicApiKey = true })
                : throw new InvalidOperationException("no lookup expected");

        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        {
            Created = req;
            CreatedFor = owner;
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
