using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Renewing connected-provider tokens (issue #289, docs/git-credentials.md "Renewing a connected
/// provider's token"). The token endpoint here rotates refresh tokens and accepts each one once,
/// which is what GitLab does; tokens are synthetic.
/// </summary>
public sealed class GitOAuthRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Owner = "owner@example.com";
    private static readonly RepoRef[] Repos = [new() { Url = "https://gitlab.example.com/group/project.git", ProviderId = "gitlab" }];

    // ------------------------------------------------------------------ fakes

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A secret with a resource version, refusing a write against a stale one like the apiserver.</summary>
    internal sealed class MemoryTokenStore : IGitTokenStore
    {
        private readonly object _gate = new();
        private Dictionary<string, byte[]> _data = new();
        private int _version;
        public int ConflictsToInject;
        public int Writes;

        public Task<GitTokenSnapshot> ReadAsync(string ownerKey, CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult(new GitTokenSnapshot(ownerKey, new Dictionary<string, byte[]>(_data),
                    _version == 0 ? null : _version.ToString()));
        }

        public Task<bool> TryWriteAsync(GitTokenSnapshot snapshot, IReadOnlyDictionary<string, byte[]> data, CancellationToken ct)
        {
            lock (_gate)
            {
                if (ConflictsToInject > 0)
                {
                    // Someone else wrote in between, e.g. another provider's sign-in.
                    ConflictsToInject--;
                    _version++;
                    return Task.FromResult(false);
                }
                if (snapshot.ResourceVersion != (_version == 0 ? null : _version.ToString())) return Task.FromResult(false);
                _data = new Dictionary<string, byte[]>(data);
                _version++;
                Writes++;
                return Task.FromResult(true);
            }
        }

        public string? Get(string key)
        {
            lock (_gate) return _data.TryGetValue(key, out var v) ? Encoding.UTF8.GetString(v) : null;
        }

        public void Seed(string access, string refresh, DateTimeOffset expires)
        {
            lock (_gate)
            {
                _data["gitlab.access"] = Encoding.UTF8.GetBytes(access);
                _data["gitlab.refresh"] = Encoding.UTF8.GetBytes(refresh);
                _data["gitlab.expires"] = Encoding.UTF8.GetBytes(expires.ToUnixTimeSeconds().ToString());
                _data["gitlab.username"] = Encoding.UTF8.GetBytes("owner");
                _version++;
            }
        }
    }

    /// <summary>In-process stand-in for the advisory lock: one holder per key.</summary>
    internal sealed class KeyedLock : IProviderLoginLock
    {
        private readonly ConcurrentDictionary<string, byte> _held = new();
        public readonly ConcurrentBag<string> Keys = new();

        public Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken ct = default)
        {
            Keys.Add(key);
            return Task.FromResult<IAsyncDisposable?>(_held.TryAdd(key, 0) ? new Release(() => _held.TryRemove(key, out _)) : null);
        }

        private sealed class Release(Action release) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>No lock at all: what the code did before, for the controls.</summary>
    internal sealed class NoLock : IProviderLoginLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(new Nothing());

        private sealed class Nothing : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    /// <summary>GitLab's token endpoint as far as refresh goes: every refresh token works once and
    /// is replaced by a new one.</summary>
    internal sealed class RotatingTokenEndpoint : HttpMessageHandler
    {
        private readonly object _gate = new();
        private int _generation;
        public string Live = "rt-0";
        public int Refreshes;
        public int InvalidGrants;
        public TimeSpan Delay = TimeSpan.Zero;
        public HttpStatusCode? FailWith;
        public string FailBody = "{\"error\":\"server_error\"}";
        public readonly int ExpiresIn = 7200;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/user"))
                return Json(HttpStatusCode.OK, "{\"username\":\"owner\"}");
            var form = await request.Content!.ReadAsStringAsync(ct);
            var fields = form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (FailWith is { } status) return Json(status, FailBody);
            lock (_gate)
            {
                if (fields["grant_type"] == "authorization_code")
                {
                    _generation++;
                    Live = $"rt-{_generation}";
                    return Json(HttpStatusCode.OK, $"{{\"access_token\":\"at-{_generation}\",\"refresh_token\":\"{Live}\",\"expires_in\":{ExpiresIn}}}");
                }
                if (fields["refresh_token"] != Live)
                {
                    InvalidGrants++;
                    return Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"The provided authorization grant is invalid\"}");
                }
                Refreshes++;
                _generation++;
                Live = $"rt-{_generation}";
                return Json(HttpStatusCode.OK, $"{{\"access_token\":\"at-{_generation}\",\"refresh_token\":\"{Live}\",\"expires_in\":{ExpiresIn},\"token_type\":\"Bearer\"}}");
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Keeps every formatted log line, to check that no token ends up in one.</summary>
    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    internal static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Git:StateKey"] = "proof-state-key",
        ["Git:Providers:0:Id"] = "gitlab",
        ["Git:Providers:0:Type"] = "gitlab",
        ["Git:Providers:0:DisplayName"] = "GitLab",
        ["Git:Providers:0:BaseUrl"] = "https://gitlab.example.com",
        ["Git:Providers:0:ClientId"] = "client",
        ["Git:Providers:0:ClientSecret"] = "client-secret"
    }).Build();

    private sealed record Rig(GitAuthService Git, MemoryTokenStore Store, RotatingTokenEndpoint Endpoint, ManualTime Time,
        RecordingLogger<GitAuthService> Log, List<(string Owner, string Provider)> Changes);

    private static Rig Make(IProviderLoginLock? locks = null, MemoryTokenStore? store = null, RotatingTokenEndpoint? endpoint = null)
    {
        store ??= new MemoryTokenStore();
        endpoint ??= new RotatingTokenEndpoint();
        var time = new ManualTime(Now);
        var log = new RecordingLogger<GitAuthService>();
        var git = new GitAuthService(Config(), new Factory(endpoint), log, store, locks ?? new KeyedLock(),
            new GitOAuthRefreshOptions { LockWait = TimeSpan.FromSeconds(10) }, time);
        var changes = new List<(string, string)>();
        git.TokensChanged += (o, p) => { lock (changes) changes.Add((o, p)); };
        return new Rig(git, store, endpoint, time, log, changes);
    }

    private static string Line(string token) => $"https://oauth2:{token}@gitlab.example.com\n";

    // ------------------------------------------------------------------ renewal

    [Fact]
    public async Task AFreshToken_IsHandedOutAsIs()
    {
        var rig = Make();
        rig.Store.Seed("at-0", "rt-0", Now.AddHours(1));

        Assert.Equal(Line("at-0"), await rig.Git.BuildCredentialStoreAsync(Owner, Repos));
        Assert.Equal(0, rig.Endpoint.Refreshes);
    }

    [Fact]
    public async Task ATokenCloseToExpiry_IsRenewed_AndTheRotatedRefreshTokenIsStored()
    {
        var rig = Make();
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(10));

        Assert.Equal(Line("at-1"), await rig.Git.BuildCredentialStoreAsync(Owner, Repos));

        Assert.Equal(1, rig.Endpoint.Refreshes);
        Assert.Equal("rt-1", rig.Store.Get("gitlab.refresh"));
        Assert.Equal(Now.AddSeconds(7200).ToUnixTimeSeconds().ToString(), rig.Store.Get("gitlab.expires"));
        Assert.Equal("owner", rig.Store.Get("gitlab.username"));
        Assert.Equal([(Owner, "gitlab")], rig.Changes);
    }

    [Fact]
    public async Task ConcurrentCallers_SpendTheRefreshTokenOnce()
    {
        var endpoint = new RotatingTokenEndpoint { Delay = TimeSpan.FromMilliseconds(300) };
        var rig = Make(endpoint: endpoint);
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(1));

        var stores = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => rig.Git.BuildCredentialStoreAsync(Owner, Repos))));

        Assert.Equal(1, endpoint.Refreshes);
        Assert.Equal(0, endpoint.InvalidGrants);
        Assert.All(stores, s => Assert.Equal(Line("at-1"), s));
        Assert.Equal("rt-1", rig.Store.Get("gitlab.refresh"));
    }

    [Fact]
    public async Task WithoutTheLock_TheLosersAreAnsweredInvalidGrant_AndOnlyTheStoredStateConverges()
    {
        // The control for the test above: the endpoint really does refuse a spent refresh token, so
        // the lock is what keeps it to one request. Without it a loser can be refused before the
        // winner has stored the rotated token, and then cannot tell that from a revoked grant: it
        // hands out nothing and briefly marks the grant refused (seen on a loaded CI runner). The
        // compare-and-swap only makes the stored state end right — the winner's write clears the
        // mark, and a later loser's mark no longer matches the refresh token it spent.
        var endpoint = new RotatingTokenEndpoint { Delay = TimeSpan.FromMilliseconds(300) };
        var rig = Make(locks: new NoLock(), endpoint: endpoint);
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(1));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => rig.Git.BuildCredentialStoreAsync(Owner, Repos))));

        Assert.True(endpoint.InvalidGrants > 0);
        Assert.Equal(endpoint.Live, rig.Store.Get("gitlab.refresh"));
        Assert.Null(rig.Store.Get("gitlab.rejected"));
    }

    [Fact]
    public async Task ABusyLock_IsReportedWithoutWaiting_ToTheBackgroundPass()
    {
        var locks = new KeyedLock();
        var rig = Make(locks: locks);
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(1));
        await using var other = await locks.TryAcquireAsync($"git-oauth/{GitAuthService.OwnerKey(Owner)}/gitlab");

        Assert.Equal(GitRefreshOutcome.Busy, await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: false));
        Assert.Equal(0, rig.Endpoint.Refreshes);
    }

    [Fact]
    public async Task TheLock_IsPerOwnerAndProvider()
    {
        var locks = new KeyedLock();
        var rig = Make(locks: locks);
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(1));

        await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: false);

        Assert.Equal($"git-oauth/{GitAuthService.OwnerKey(Owner)}/gitlab", Assert.Single(locks.Keys.Distinct()));
    }

    [Fact]
    public async Task AConflictingWrite_IsRetried_SoTheOnlyLiveRefreshTokenIsNotLost()
    {
        var store = new MemoryTokenStore();
        var rig = Make(store: store);
        store.Seed("at-0", "rt-0", Now.AddMinutes(1));
        store.ConflictsToInject = 2;

        Assert.Equal(GitRefreshOutcome.Refreshed, await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: true));

        Assert.Equal("rt-1", store.Get("gitlab.refresh"));
        Assert.Equal("at-1", store.Get("gitlab.access"));
    }

    // ------------------------------------------------------------------ refusal

    [Fact]
    public async Task InvalidGrant_MarksTheGrantRefused_AndSessionsGetNoDeadToken()
    {
        var rig = Make();
        rig.Store.Seed("at-0", "rt-stale", Now.AddMinutes(1));

        Assert.Equal(GitRefreshOutcome.Rejected, await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: true));

        Assert.NotNull(rig.Store.Get("gitlab.rejected"));
        Assert.Null(await rig.Git.BuildCredentialStoreAsync(Owner, Repos));
        Assert.False(await rig.Git.IsConnectedAsync(Owner, "gitlab"));
        var info = Assert.Single(await rig.Git.ListProvidersAsync(Owner));
        Assert.True(info.Connected);
        Assert.True(info.NeedsSignIn);
        Assert.Equal([(Owner, "gitlab")], rig.Changes);
        // Refused is final until a sign-in: no further requests with the dead token.
        Assert.Equal(GitRefreshOutcome.NotDue, await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: true));
        Assert.Equal(1, rig.Endpoint.InvalidGrants);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}")]
    [InlineData(HttpStatusCode.InternalServerError, "<html>oops</html>")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    public async Task OtherFailures_AreRetried_NotCalledARefusal(HttpStatusCode status, string body)
    {
        var rig = Make(endpoint: new RotatingTokenEndpoint { FailWith = status, FailBody = body });
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(1));

        Assert.Equal(GitRefreshOutcome.Failed, await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: true));

        Assert.Null(rig.Store.Get("gitlab.rejected"));
        Assert.Equal("rt-0", rig.Store.Get("gitlab.refresh"));
        // Still within its lifetime, so the stored token is still the best there is.
        Assert.Equal(Line("at-0"), await rig.Git.BuildCredentialStoreAsync(Owner, Repos));
        Assert.Empty(rig.Changes);
    }

    [Fact]
    public async Task SigningInAgain_ClearsTheRefusal_AndTellsTheRunningSessions()
    {
        var rig = Make();
        rig.Store.Seed("at-0", "rt-stale", Now.AddMinutes(1));
        await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: true);
        rig.Changes.Clear();

        var url = new Uri(rig.Git.CreateAuthorizeUrl("gitlab", Owner, "https://hub.example.com/api/git/callback/gitlab"));
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query)["state"].ToString();
        Assert.Equal(Owner, await rig.Git.HandleCallbackAsync("gitlab", "code", state, "https://hub.example.com/api/git/callback/gitlab"));

        Assert.Null(rig.Store.Get("gitlab.rejected"));
        Assert.True(await rig.Git.IsConnectedAsync(Owner, "gitlab"));
        Assert.False(Assert.Single(await rig.Git.ListProvidersAsync(Owner)).NeedsSignIn);
        Assert.Equal([(Owner, "gitlab")], rig.Changes);
    }

    [Fact]
    public async Task Disconnecting_TellsTheRunningSessions()
    {
        var rig = Make();
        rig.Store.Seed("at-0", "rt-0", Now.AddHours(1));

        await rig.Git.DisconnectAsync(Owner, "gitlab");

        Assert.Null(rig.Store.Get("gitlab.access"));
        Assert.Equal([(Owner, "gitlab")], rig.Changes);
    }

    [Fact]
    public async Task NoTokenEverReachesTheLog()
    {
        var rig = Make(endpoint: new RotatingTokenEndpoint());
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(1));
        await rig.Git.BuildCredentialStoreAsync(Owner, Repos);
        rig.Time.Now = Now.AddHours(3);
        rig.Store.Seed("at-x", "rt-stale", Now.AddMinutes(1));
        await rig.Git.RefreshIfDueAsync(Owner, "gitlab", waitForLock: true);

        Assert.NotEmpty(rig.Log.Lines);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("at-") || l.Contains("rt-") || l.Contains("client-secret") || l.Contains(Owner));
    }

    // ------------------------------------------------------------------ handing the token on

    private sealed class Sessions : IGitSessionCredentials
    {
        public List<GitSessionTarget> Running = new();
        public readonly ConcurrentQueue<string> Installed = new();
        public readonly HashSet<string> Unreachable = new();

        public Task<IReadOnlyList<GitSessionTarget>> ListRunningAsync(string? owner, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitSessionTarget>>(Running.Where(t => owner is null || t.Owner == owner).ToList());

        public Task<bool> ReinstallAsync(string sessionId, CancellationToken ct)
        {
            if (Unreachable.Contains(sessionId)) throw new HttpRequestException("pod unreachable");
            Installed.Enqueue(sessionId);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task APass_RenewsEachGrantARunningSessionUsesOnce_AndHandsItToThatOwnersSessionsOnly()
    {
        var rig = Make();
        rig.Store.Seed("at-0", "rt-0", Now.AddMinutes(1));
        var sessions = new Sessions
        {
            Running =
            [
                new("s1", Owner, ["gitlab"]),
                new("s2", Owner, ["gitlab"]),
                new("s3", "someone-else@example.com", ["gitlab"]),
                new("s4", Owner, ["github"])
            ]
        };
        var refresher = new GitOAuthRefresher(rig.Git, rig.Git, sessions, NullLogger<GitOAuthRefresher>.Instance);

        Assert.Equal(1, await refresher.RefreshDueAsync(CancellationToken.None));
        // Both owners' secrets are the same MemoryTokenStore here, so the second owner finds the
        // renewed token not due; what matters is one request for the shared grant.
        Assert.Equal(1, rig.Endpoint.Refreshes);

        Assert.True(refresher.Changes.TryRead(out var change));
        Assert.Equal((Owner, "gitlab"), change);
        Assert.Equal(2, await refresher.DistributeAsync(change.Owner, change.ProviderId, CancellationToken.None));
        Assert.Equal(["s1", "s2"], sessions.Installed.OrderBy(s => s));
    }

    [Fact]
    public async Task ASessionTheChangeDidNotReach_IsRetriedOnTheNextPass()
    {
        var rig = Make();
        rig.Store.Seed("at-0", "rt-0", Now.AddHours(1));
        var sessions = new Sessions { Running = [new("s1", Owner, ["gitlab"])] };
        sessions.Unreachable.Add("s1");
        var refresher = new GitOAuthRefresher(rig.Git, rig.Git, sessions, NullLogger<GitOAuthRefresher>.Instance);

        Assert.Equal(0, await refresher.DistributeAsync(Owner, "gitlab", CancellationToken.None));
        sessions.Unreachable.Clear();
        await refresher.RefreshDueAsync(CancellationToken.None);

        Assert.Equal(["s1"], sessions.Installed);
    }
}
