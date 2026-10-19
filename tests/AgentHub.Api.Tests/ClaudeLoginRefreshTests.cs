using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The hub as the single owner of each Claude login (issue #116, docs/provider-accounts.md "The
/// hub owns each login"). Tokens here are synthetic; the behaviour of the real CLI the rules rely
/// on was observed against Claude Code 2.1.287 and is recorded in the issue.
/// </summary>
public sealed class ClaudeLoginRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Secret = KubernetesSessionService.ProviderSecretName("owner", AgentKind.Claude);

    private static byte[] File(string access, string refresh, DateTimeOffset expires, string extra = "") =>
        Encoding.UTF8.GetBytes(
            $"{{\"claudeAiOauth\":{{\"accessToken\":\"{access}\",\"refreshToken\":\"{refresh}\"," +
            $"\"expiresAt\":{expires.ToUnixTimeMilliseconds()},\"scopes\":[\"user:inference\",\"user:profile\"]," +
            $"\"subscriptionType\":\"max\"{extra}}}}}");

    private static readonly byte[] Blanked =
        Encoding.UTF8.GetBytes("{\"claudeAiOauth\":{\"accessToken\":\"\",\"refreshToken\":\"\",\"expiresAt\":0,\"scopes\":[\"user:inference\"]}}");

    private static string RefreshTokenOf(byte[] file) => ClaudeCredentialFile.Read(file)!.RefreshToken!;

    // ---------------------------------------------------------------- the credential file

    [Fact]
    public void Read_SeesTheBlankedFileTheCliLeavesAfterInvalidGrant()
    {
        Assert.True(ClaudeCredentialFile.Read(Blanked)!.IsBlank);
        Assert.True(ClaudeCredentialFile.IsBlank(Blanked));
        Assert.False(ClaudeCredentialFile.IsBlank(File("at-1", "rt-1", Now)));
        Assert.True(ClaudeCredentialFile.IsBlank(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void IsOlder_ComparesExpiry_AndNeverCallsAFileWithoutOneStale()
    {
        var older = File("at-1", "rt-1", Now.AddHours(1));
        var newer = File("at-2", "rt-2", Now.AddHours(8));

        Assert.True(ClaudeCredentialFile.IsOlder(older, newer));
        Assert.False(ClaudeCredentialFile.IsOlder(newer, older));
        Assert.False(ClaudeCredentialFile.IsOlder(older, Encoding.UTF8.GetBytes("{\"claudeAiOauth\":{\"accessToken\":\"x\"}}")));
        // A blanked stored file is no reason to refuse a working login.
        Assert.False(ClaudeCredentialFile.IsOlder(older, Blanked));
    }

    [Fact]
    public void WithRefreshedTokens_ReplacesTheTokens_AndKeepsEveryOtherField()
    {
        var stored = File("at-1", "rt-1", Now, ",\"rateLimitTier\":\"tier\",\"futureField\":{\"a\":1}");

        var renewed = ClaudeCredentialFile.WithRefreshedTokens(stored,
            new ClaudeTokenResponse("at-2", "rt-2", 28800, 2592000, ["user:inference", "user:profile", "user:sessions:claude_code"]), Now);

        var oauth = JsonNode.Parse(renewed)!["claudeAiOauth"]!;
        Assert.Equal("at-2", oauth["accessToken"]!.GetValue<string>());
        Assert.Equal("rt-2", oauth["refreshToken"]!.GetValue<string>());
        Assert.Equal(Now.AddHours(8).ToUnixTimeMilliseconds(), oauth["expiresAt"]!.GetValue<long>());
        Assert.Equal(Now.AddDays(30).ToUnixTimeMilliseconds(), oauth["refreshTokenExpiresAt"]!.GetValue<long>());
        Assert.Equal(3, oauth["scopes"]!.AsArray().Count);
        Assert.Equal("max", oauth["subscriptionType"]!.GetValue<string>());
        Assert.Equal("tier", oauth["rateLimitTier"]!.GetValue<string>());
        Assert.Equal(1, oauth["futureField"]!["a"]!.GetValue<int>());
    }

    [Fact]
    public void WithRefreshedTokens_KeepsTheRefreshToken_WhenTheResponseCarriesNone()
    {
        var renewed = ClaudeCredentialFile.WithRefreshedTokens(File("at-1", "rt-1", Now),
            new ClaudeTokenResponse("at-2", null, 3600, null, null), Now);

        Assert.Equal("rt-1", RefreshTokenOf(renewed));
        Assert.Equal(2, JsonNode.Parse(renewed)!["claudeAiOauth"]!["scopes"]!.AsArray().Count);
    }

    // ---------------------------------------------------------------- writeback: newer wins

    [Fact]
    public void Attach_KeepsTheStoredClaudeLogin_WhenAPodUploadsAnOlderGeneration()
    {
        var set = new ProviderAccountSet();
        var first = ProviderAccountSecret.Attach(set, File("at-2", "rt-2", Now.AddHours(8)), null, null, agent: AgentKind.Claude);
        set.Dirty = false;

        var stale = ProviderAccountSecret.Attach(set, File("at-1", "rt-1", Now.AddHours(1)), null, first.AccountId, agent: AgentKind.Claude);

        Assert.True(stale.Stale);
        Assert.Equal(first.AccountId, stale.AccountId);
        Assert.False(set.Dirty);
        Assert.Equal("rt-2", RefreshTokenOf(set.Files[first.AccountId]));
    }

    [Fact]
    public void Attach_TakesANewerClaudeLogin_AndClearsARejectedMark()
    {
        var set = new ProviderAccountSet();
        var first = ProviderAccountSecret.Attach(set, File("at-1", "rt-1", Now), null, null, agent: AgentKind.Claude);
        set.Find(first.AccountId)!.LoginRejectedAt = Now.UtcDateTime;

        var newer = ProviderAccountSecret.Attach(set, File("at-2", "rt-2", Now.AddHours(8)), null, first.AccountId, agent: AgentKind.Claude);

        Assert.False(newer.Stale);
        Assert.Equal("rt-2", RefreshTokenOf(set.Files[first.AccountId]));
        Assert.Null(set.Find(first.AccountId)!.LoginRejectedAt);
        Assert.False(ProviderAccountInfo.From(set.Find(first.AccountId)!).NeedsLogin);
    }

    [Fact]
    public void Attach_WithoutAnAgent_KeepsLastWriterWins()
    {
        var set = new ProviderAccountSet();
        var first = ProviderAccountSecret.Attach(set, File("at-2", "rt-2", Now.AddHours(8)), null, null);

        ProviderAccountSecret.Attach(set, File("at-1", "rt-1", Now), null, first.AccountId);

        Assert.Equal("rt-1", RefreshTokenOf(set.Files[first.AccountId]));
    }

    // ---------------------------------------------------------------- the refresher

    [Fact]
    public async Task ADueLogin_IsRenewed_StoredWithItsNewRefreshToken_AndHandedToEveryRunningSessionOnIt()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-1", "rt-1", Now.AddMinutes(30)));
        var other = rig.Add(File("at-9", "rt-9", Now.AddHours(7)));
        rig.Sessions.Add((account, new ProviderLoginSession("s1", "10.0.0.1", "cb-1")));
        rig.Sessions.Add((account, new ProviderLoginSession("s2", "10.0.0.2", "cb-2")));
        rig.Sessions.Add((other, new ProviderLoginSession("s3", "10.0.0.3", "cb-3")));

        var renewed = await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None);

        Assert.Equal(1, renewed);
        Assert.Equal(["rt-1"], rig.Endpoint.Redeemed);
        var stored = rig.StoredFile(account);
        Assert.Equal("rt-2", RefreshTokenOf(stored));
        Assert.Equal(Now.AddHours(8), ClaudeCredentialFile.Read(stored)!.ExpiresAt);
        Assert.Equal(Now.UtcDateTime, rig.Stored().Find(account)!.RefreshedAt);
        Assert.Equal(["s1", "s2"], rig.Pusher.Refreshed.Select(p => p.Session).Order());
        Assert.All(rig.Pusher.Refreshed, p => Assert.Equal("rt-2", RefreshTokenOf(p.File)));
        // The untouched account stays as it was.
        Assert.Equal("rt-9", RefreshTokenOf(rig.StoredFile(other)));
    }

    [Fact]
    public async Task ALoginNobodyUses_IsRenewedAsWell()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-1", "rt-1", Now.AddMinutes(10)));

        Assert.Equal(1, await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None));
        Assert.Equal("rt-2", RefreshTokenOf(rig.StoredFile(account)));
        Assert.Empty(rig.Pusher.Refreshed);
    }

    [Fact]
    public async Task LoginsOutsideTheMargin_BlankedOnes_AndOnesRenewedJustNow_AreLeftAlone()
    {
        var rig = new Rig();
        rig.Add(File("at-1", "rt-1", Now.AddHours(3)));
        rig.Add(Blanked);
        var recent = rig.Add(File("at-3", "rt-3", Now.AddMinutes(20)));
        rig.Mutate(set => set.Find(recent)!.RefreshedAt = Now.UtcDateTime.AddMinutes(-5));

        Assert.Equal(0, await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None));
        Assert.Empty(rig.Endpoint.Redeemed);
    }

    [Fact]
    public async Task ARejectedLogin_IsMarkedAsNeedingASignIn_AndNotTriedAgain()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-1", "rt-1", Now.AddMinutes(30)));
        rig.Sessions.Add((account, new ProviderLoginSession("s1", "10.0.0.1", "cb-1")));
        rig.Endpoint.Next = _ => new ClaudeRefreshResult(ClaudeRefreshStatus.Rejected, Detail: "invalid_grant");

        Assert.Equal(0, await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None));
        Assert.Equal(0, await rig.Refresher.RefreshDueAsync(Now.AddMinutes(5), CancellationToken.None));

        Assert.Single(rig.Endpoint.Redeemed);
        Assert.True(ProviderAccountInfo.From(rig.Stored().Find(account)!).NeedsLogin);
        Assert.Equal("rt-1", RefreshTokenOf(rig.StoredFile(account)));
        Assert.Empty(rig.Pusher.Refreshed);
    }

    [Fact]
    public async Task ATransientFailure_ChangesNothing_AndIsRetriedNextPass()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-1", "rt-1", Now.AddMinutes(30)));
        rig.Endpoint.Next = _ => new ClaudeRefreshResult(ClaudeRefreshStatus.Failed, Detail: "HTTP 503");

        await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None);
        rig.Endpoint.Next = null;
        await rig.Refresher.RefreshDueAsync(Now.AddMinutes(5), CancellationToken.None);

        Assert.Equal(["rt-1", "rt-1"], rig.Endpoint.Redeemed);
        Assert.False(ProviderAccountInfo.From(rig.Stored().Find(account)!).NeedsLogin);
        Assert.Equal("rt-2", RefreshTokenOf(rig.StoredFile(account)));
    }

    [Fact]
    public async Task ALoginThatChangedWhileTheRequestWasOut_IsKept_AndTheRenewedTokensDropped()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-1", "rt-1", Now.AddMinutes(30)));
        rig.Endpoint.Next = token =>
        {
            // A sign-in in a session lands while the hub waits for the provider.
            rig.Mutate(set => set.Files[account] = File("at-x", "rt-x", Now.AddHours(8)));
            return Rig.Issue(token, 2);
        };

        Assert.Equal(0, await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None));
        Assert.Equal("rt-x", RefreshTokenOf(rig.StoredFile(account)));
        Assert.Empty(rig.Pusher.Refreshed);
    }

    [Fact]
    public async Task AConflictingWrite_IsRetriedFromAFreshRead_WithoutLosingTheOtherChange()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-1", "rt-1", Now.AddMinutes(30)));
        // Another writer changes the label between the refresher's read and its first write.
        rig.Repository.BeforeWrite = () =>
        {
            rig.Repository.BeforeWrite = null;
            rig.Mutate(set => set.Find(account)!.Label = "renamed meanwhile");
        };

        Assert.Equal(1, await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None));
        Assert.Equal("rt-2", RefreshTokenOf(rig.StoredFile(account)));
        Assert.Equal("renamed meanwhile", rig.Stored().Find(account)!.Label);
    }

    [Fact]
    public async Task ALoginAnotherReplicaIsRenewing_IsSkipped()
    {
        var rig = new Rig();
        rig.Add(File("at-1", "rt-1", Now.AddMinutes(30)));
        rig.Locks.Held = true;

        Assert.Equal(0, await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None));
        Assert.Empty(rig.Endpoint.Redeemed);
    }

    [Fact]
    public async Task APodThatCannotTakeTheRenewal_OrFails_DoesNotStopTheOthers()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-1", "rt-1", Now.AddMinutes(30)));
        rig.Sessions.Add((account, new ProviderLoginSession("old-image", "10.0.0.1", "cb-1")));
        rig.Sessions.Add((account, new ProviderLoginSession("unreachable", "10.0.0.2", "cb-2")));
        rig.Sessions.Add((account, new ProviderLoginSession("current", "10.0.0.3", "cb-3")));
        rig.Pusher.Answer = session => session switch
        {
            "old-image" => false,
            "unreachable" => throw new HttpRequestException("connection refused"),
            _ => true
        };

        Assert.Equal(1, await rig.Refresher.RefreshDueAsync(Now, CancellationToken.None));
        Assert.Equal(3, rig.Pusher.Refreshed.Count);
    }

    [Fact]
    public async Task AWritebackThatWon_IsHandedToTheAccountsOtherSessions()
    {
        var rig = new Rig();
        var uploaded = File("at-5", "rt-5", Now.AddHours(8));
        var account = rig.Add(uploaded);
        rig.Sessions.Add((account, new ProviderLoginSession("source", "10.0.0.1", "cb-1")));
        rig.Sessions.Add((account, new ProviderLoginSession("sibling", "10.0.0.2", "cb-2")));

        await rig.Refresher.SyncAfterWritebackAsync(Session("source"), account, uploaded, CancellationToken.None);

        var pushed = Assert.Single(rig.Pusher.Refreshed);
        Assert.Equal("sibling", pushed.Session);
        Assert.Equal("rt-5", RefreshTokenOf(pushed.File));
    }

    [Fact]
    public async Task AWritebackThatLost_GetsTheStoredLoginBack_AndNobodyElseIsTouched()
    {
        var rig = new Rig();
        var account = rig.Add(File("at-5", "rt-5", Now.AddHours(8)));
        rig.Sessions.Add((account, new ProviderLoginSession("source", "10.0.0.1", "cb-1")));
        rig.Sessions.Add((account, new ProviderLoginSession("sibling", "10.0.0.2", "cb-2")));

        await rig.Refresher.SyncAfterWritebackAsync(Session("source"), account, File("at-1", "rt-1", Now.AddHours(1)),
            CancellationToken.None);

        var pushed = Assert.Single(rig.Pusher.Refreshed);
        Assert.Equal("source", pushed.Session);
        Assert.Equal("rt-5", RefreshTokenOf(pushed.File));
    }

    private static SessionRecord Session(string id) => new()
    {
        Id = id, Owner = "owner", Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription, CallbackToken = "cb"
    };

    // ---------------------------------------------------------------- the token endpoint

    [Fact]
    public async Task TokenEndpoint_SendsTheRequestTheCliSends_AndReadsTheRenewedTokens()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var endpoint = Endpoint(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK,
                "{\"access_token\":\"at-2\",\"refresh_token\":\"rt-2\",\"expires_in\":28800,\"scope\":\"user:inference user:profile\"}");
        });

        var result = await endpoint.RefreshAsync("rt-1", ["user:inference", "user:profile"], null, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal(ClaudeTokenEndpoint.DefaultTokenUrl, sent.RequestUri!.ToString());
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("refresh_token", json.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("rt-1", json.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal(ClaudeTokenEndpoint.DefaultClientId, json.RootElement.GetProperty("client_id").GetString());
        Assert.Equal("user:inference user:profile", json.RootElement.GetProperty("scope").GetString());
        Assert.Equal(ClaudeRefreshStatus.Refreshed, result.Status);
        Assert.Equal("rt-2", result.Tokens!.RefreshToken);
        Assert.Equal(28800, result.Tokens.ExpiresInSeconds);
        Assert.Equal(["user:inference", "user:profile"], result.Tokens.Scopes);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"Refresh token not found or invalid\"}", ClaudeRefreshStatus.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_scope\"}", ClaudeRefreshStatus.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", ClaudeRefreshStatus.Failed)]
    [InlineData(HttpStatusCode.OK, "{\"access_token\":\"\",\"expires_in\":0}", ClaudeRefreshStatus.Failed)]
    [InlineData(HttpStatusCode.OK, "not json", ClaudeRefreshStatus.Failed)]
    public async Task TokenEndpoint_OnlyInvalidGrantMeansTheLoginIsGone(HttpStatusCode status, string answer, ClaudeRefreshStatus expected)
    {
        var endpoint = Endpoint(_ => Task.FromResult(Json(status, answer)));

        var result = await endpoint.RefreshAsync("rt-1", [], "custom-client", CancellationToken.None);

        Assert.Equal(expected, result.Status);
        // Nothing the provider echoed beyond its error code ends up in what gets logged.
        Assert.DoesNotContain("Refresh token", result.Detail ?? "");
    }

    [Fact]
    public async Task TokenEndpoint_TakesTheUrlFromConfiguration_AndAFilesOwnClientId()
    {
        string? body = null;
        Uri? uri = null;
        var endpoint = Endpoint(async request =>
        {
            uri = request.RequestUri;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"access_token\":\"a\",\"expires_in\":60}");
        }, new Dictionary<string, string?> { ["AgentHub:ClaudeLoginRefresh:TokenUrl"] = "http://127.0.0.1:1/v1/oauth/token" });

        await endpoint.RefreshAsync("rt-1", [], "custom-client", CancellationToken.None);

        Assert.Equal("http://127.0.0.1:1/v1/oauth/token", uri!.ToString());
        Assert.Contains("\"client_id\":\"custom-client\"", body);
        Assert.DoesNotContain("scope", body);
    }

    private static ClaudeTokenEndpoint Endpoint(Func<HttpRequestMessage, Task<HttpResponseMessage>> send,
        Dictionary<string, string?>? settings = null) =>
        new(new HttpClient(new DelegateHandler(send)),
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }

    // ---------------------------------------------------------------- fakes

    private sealed class Rig
    {
        public FakeRepository Repository { get; } = new();
        public FakeEndpoint Endpoint { get; } = new();
        public FakeLock Locks { get; } = new();
        public FakePusher Pusher { get; } = new();
        public List<(string Account, ProviderLoginSession Session)> Sessions => Repository.Sessions;
        public ClaudeLoginRefresher Refresher { get; }

        public Rig()
        {
            Refresher = new ClaudeLoginRefresher(Repository, Endpoint, Locks, Pusher,
                new ClaudeLoginRefreshOptions(), NullLogger<ClaudeLoginRefresher>.Instance);
        }

        public string Add(byte[] file)
        {
            string id = "";
            Mutate(set => id = ProviderAccountSecret.Attach(set, file, new ProviderAccountIdentity($"key-{set.Accounts.Count}", null, null), null).AccountId);
            return id;
        }

        public void Mutate(Action<ProviderAccountSet> change)
        {
            var set = ProviderAccountSecret.Read(Repository.Data, AgentKind.Claude);
            change(set);
            Repository.Data = ProviderAccountSecret.Write(set, AgentKind.Claude);
            Repository.Version++;
        }

        public ProviderAccountSet Stored() => ProviderAccountSecret.Read(Repository.Data, AgentKind.Claude);

        public byte[] StoredFile(string account) => Stored().Files[account];

        public static ClaudeRefreshResult Issue(string refreshToken, int generation) =>
            new(ClaudeRefreshStatus.Refreshed, new ClaudeTokenResponse($"at-{generation}", $"rt-{generation}", 28800, null, null));
    }

    private sealed class FakeRepository : IProviderLoginRepository
    {
        public Dictionary<string, byte[]>? Data { get; set; }
        public int Version { get; set; }
        public Action? BeforeWrite { get; set; }
        public List<(string Account, ProviderLoginSession Session)> Sessions { get; } = new();

        public Task<IReadOnlyList<string>> ListSecretNamesAsync(AgentKind agent, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Data is null ? [] : [Secret]);

        public Task<ProviderLoginSnapshot?> ReadAsync(string secretName, AgentKind agent, CancellationToken ct)
        {
            if (Data is null || secretName != Secret) return Task.FromResult<ProviderLoginSnapshot?>(null);
            var set = ProviderAccountSecret.Read(new Dictionary<string, byte[]>(Data), agent);
            set.ResourceVersion = Version.ToString();
            return Task.FromResult<ProviderLoginSnapshot?>(new ProviderLoginSnapshot(secretName, set));
        }

        public Task<bool> TryWriteAsync(ProviderLoginSnapshot snapshot, AgentKind agent, CancellationToken ct)
        {
            BeforeWrite?.Invoke();
            if (snapshot.Accounts.ResourceVersion != Version.ToString()) return Task.FromResult(false);
            Data = ProviderAccountSecret.Write(snapshot.Accounts, agent);
            Version++;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ProviderLoginSession>> ListRunningSessionsAsync(string secretName, AgentKind agent,
            string accountId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderLoginSession>>(
                Sessions.Where(s => s.Account == accountId).Select(s => s.Session).ToList());
    }

    private sealed class FakeEndpoint : IClaudeTokenEndpoint
    {
        private int _generation = 1;
        public List<string> Redeemed { get; } = new();
        public Func<string, ClaudeRefreshResult>? Next { get; set; }

        public Task<ClaudeRefreshResult> RefreshAsync(string refreshToken, IReadOnlyList<string> scopes, string? clientId,
            CancellationToken ct)
        {
            Redeemed.Add(refreshToken);
            return Task.FromResult(Next?.Invoke(refreshToken) ?? Rig.Issue(refreshToken, ++_generation));
        }
    }

    private sealed class FakeLock : IProviderLoginLock
    {
        public bool Held { get; set; }

        public Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(Held ? null : new Releaser());

        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakePusher : IAgentCredentialPusher
    {
        public List<(string Session, byte[] File)> Refreshed { get; } = new();
        public Func<string, bool>? Answer { get; set; }

        public Task PushAsync(string sessionId, string podIp, string callbackToken, AgentKind agent, byte[] file, string? reason, CancellationToken ct)
            => throw new NotSupportedException("A renewal never restarts the agent.");

        public Task<bool> RefreshAsync(string sessionId, string podIp, string callbackToken, AgentKind agent, byte[] file, CancellationToken ct)
        {
            lock (Refreshed) Refreshed.Add((sessionId, file));
            return Task.FromResult(Answer?.Invoke(sessionId) ?? true);
        }
    }
}
