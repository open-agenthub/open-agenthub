using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// A pod's usage-limit report and what the hub makes of it (docs/account-limits.md): which
/// account is marked, for how long, and the endpoint that takes the report.
/// </summary>
public sealed class AccountFailoverTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static SessionRecord Session(string? credentialId = null, string? resolved = "work0001",
        AgentAuthMode auth = AgentAuthMode.Subscription) => new()
    {
        Id = "s1", Owner = "alice", Title = "Coder", CallbackToken = "callback-token",
        Agent = AgentKind.Claude, AuthMode = auth, Mode = SessionMode.Interactive, Status = "Running",
        CredentialId = credentialId, ResolvedCredentialId = resolved
    };

    private static AccountFailover Failover(FakeSessions sessions, FakeNotifier notifier, int? defaultSeconds = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentHub:AccountExhaustedDefaultSeconds"] = defaultSeconds?.ToString()
        }).Build();
        return new AccountFailover(sessions, [notifier], configuration, NullLogger<AccountFailover>.Instance, () => Now);
    }

    [Fact]
    public async Task MarksTheResolvedAccountOfAnUnpinnedSession_ForTheDefaultHour_AndNotifies()
    {
        var sessions = new FakeSessions();
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(),
            new AccountExhaustedReport("output", Detail: "You've hit your usage limit"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Marked, outcome.Action);
        Assert.Equal("work0001", outcome.AccountId);
        Assert.Equal(Now.AddHours(1), outcome.ExhaustedUntil);
        var marked = Assert.Single(sessions.Marked);
        Assert.Equal(("alice", AgentKind.Claude, "work0001", Now.AddHours(1)), (marked.Owner, marked.Agent, marked.Id, marked.Until));
        Assert.Equal("output You've hit your usage limit", marked.Reason);
        var (_, ev, message) = Assert.Single(notifier.Events);
        Assert.Equal(AccountFailover.ExhaustedEvent, ev);
        Assert.Contains("\"Work\"", message);
    }

    [Fact]
    public async Task ThePinWinsOverTheResolvedAccount_AndTheReportsResetTimeOverTheDefault()
    {
        var sessions = new FakeSessions();

        var outcome = await Failover(sessions, new FakeNotifier()).HandleAsync(Session(credentialId: "home0002"),
            new AccountExhaustedReport("mod", "five_hour", 100, Now.AddHours(3)), CancellationToken.None);

        Assert.Equal("home0002", outcome.AccountId);
        Assert.Equal(Now.AddHours(3), outcome.ExhaustedUntil);
        Assert.Equal("mod five_hour 100%", Assert.Single(sessions.Marked).Reason);
    }

    [Fact]
    public void AResetTimeIsBounded_PastIsTheDefault_FarFutureIsClamped()
    {
        var failover = Failover(new FakeSessions(), new FakeNotifier(), defaultSeconds: 600);

        Assert.Equal(Now.AddMinutes(10), failover.ExhaustedUntil(new AccountExhaustedReport("mod"), Now));
        Assert.Equal(Now.AddMinutes(10), failover.ExhaustedUntil(new AccountExhaustedReport("mod", ResetsAt: Now.AddMinutes(-5)), Now));
        Assert.Equal(Now + AccountFailover.MaxExhaustedFor,
            failover.ExhaustedUntil(new AccountExhaustedReport("mod", ResetsAt: Now.AddYears(1)), Now));
        // An unspecified kind is read as UTC — the pod sends ISO strings with a Z.
        Assert.Equal(Now.AddHours(2),
            failover.ExhaustedUntil(new AccountExhaustedReport("mod", ResetsAt: DateTime.SpecifyKind(Now.AddHours(2), DateTimeKind.Unspecified)), Now));
    }

    [Theory]
    [InlineData(AgentAuthMode.ApiKey, "work0001")]
    [InlineData(AgentAuthMode.Subscription, null)]
    public async Task IgnoresSessionsWithoutAnAccount(AgentAuthMode auth, string? resolved)
    {
        var sessions = new FakeSessions();
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(resolved: resolved, auth: auth),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Ignored, outcome.Action);
        Assert.Empty(sessions.Marked);
        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_IsIgnoredWithoutANotification()
    {
        var sessions = new FakeSessions { Missing = true };
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Ignored, outcome.Action);
        Assert.Empty(notifier.Events);
    }

    // ------------------------------------------------------------------ the endpoint

    [Fact]
    public async Task Endpoint_AuthenticatesWithTheCallbackToken_AndForwardsTheRecord()
    {
        var failover = new RecordingFailover();
        var controller = Internal(Session(), failover, token: "callback-token");

        var result = await controller.AccountExhausted("s1",
            new AccountExhaustedReport("mod", "five_hour", 100), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(AccountFailoverOutcome.Marked, Assert.IsType<AccountFailoverOutcome>(ok.Value).Action);
        Assert.Equal("s1", failover.Session!.Id);
        Assert.Equal("five_hour", failover.Report!.Kind);
    }

    [Fact]
    public async Task Endpoint_RefusesAWrongToken_AndAnUnknownSource()
    {
        var failover = new RecordingFailover();

        Assert.IsType<UnauthorizedResult>(await Internal(Session(), failover, token: "wrong")
            .AccountExhausted("s1", new AccountExhaustedReport("mod"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await Internal(Session(), failover, token: "callback-token")
            .AccountExhausted("s1", new AccountExhaustedReport("guess"), CancellationToken.None));
        Assert.Null(failover.Session);
    }

    [Fact]
    public async Task Endpoint_WithoutTheService_AnswersIgnored()
    {
        var result = await Internal(Session(), null, token: "callback-token")
            .AccountExhausted("s1", new AccountExhaustedReport("output"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(AccountFailoverOutcome.Ignored, Assert.IsType<AccountFailoverOutcome>(ok.Value).Action);
    }

    private static InternalController Internal(SessionRecord session, IAccountFailover? failover, string token)
    {
        var controller = new InternalController(new TokenStore(session), [], new FakeSessions(), null!, [], [], null!, null!,
            accountFailover: failover);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Headers["X-Agent-Token"] = token;
        return controller;
    }

    // ------------------------------------------------------------------ fakes

    private sealed class RecordingFailover : IAccountFailover
    {
        public SessionRecord? Session { get; private set; }
        public AccountExhaustedReport? Report { get; private set; }
        public Task<AccountFailoverOutcome> HandleAsync(SessionRecord session, AccountExhaustedReport report, CancellationToken ct)
        {
            Session = session;
            Report = report;
            return Task.FromResult(new AccountFailoverOutcome("work0001", Now.AddHours(1), AccountFailoverOutcome.Marked));
        }
    }

    private sealed class TokenStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default)
            => Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class FakeNotifier : INotifier
    {
        public List<(SessionRecord Session, string Event, string Message)> Events { get; } = [];
        public Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
        {
            Events.Add((s, eventType, message));
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeSessions : ISessionService
    {
        public bool Missing { get; init; }
        public List<(string Owner, AgentKind Agent, string Id, DateTime Until, string? Reason)> Marked { get; } = [];

        public Task<ProviderAccountInfo?> MarkProviderAccountExhaustedAsync(string owner, AgentKind agent, string id,
            DateTime until, string? reason, CancellationToken ct = default)
        {
            if (Missing) return Task.FromResult<ProviderAccountInfo?>(null);
            Marked.Add((owner, agent, id, until, reason));
            return Task.FromResult<ProviderAccountInfo?>(new(id, "Work", null, null, Now, null, true, until, reason, true));
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
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
