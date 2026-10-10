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
/// account is marked, for how long, whether the session is moved and to which account, what
/// the session and the notifiers are told, and the endpoint that takes the report.
/// </summary>
public sealed class AccountFailoverTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static readonly ProviderAccountInfo Work = new("work0001", "Work", "w@example.com", null, Now, Now.AddHours(-1), true);
    private static readonly ProviderAccountInfo Home = new("home0002", "Home", null, null, Now, Now.AddDays(-3), false);
    private static readonly ProviderAccountInfo Spare = new("spare003", "Spare", null, null, Now, null, false);

    private static SessionRecord Session(string? credentialId = null, string? resolved = "work0001",
        AgentAuthMode auth = AgentAuthMode.Subscription, string? failover = null) => new()
    {
        Id = "s1", Owner = "alice", Title = "Coder", CallbackToken = "callback-token", ProjectId = "proj",
        Agent = AgentKind.Claude, AuthMode = auth, Mode = SessionMode.Interactive, Status = "Running",
        CredentialId = credentialId, ResolvedCredentialId = resolved, AccountFailover = failover
    };

    private static SessionInfo Running(string phase = "Running", string? podIp = "10.0.0.5") => new()
    {
        Id = "s1", Title = "Coder", Owner = "alice", Mode = SessionMode.Interactive, Phase = phase, PodIp = podIp,
        Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription
    };

    private static FakeSessions Sessions(params ProviderAccountInfo[] claude) => new()
    {
        Info = Running(),
        Accounts = new Dictionary<string, IReadOnlyList<ProviderAccountInfo>> { ["Claude"] = claude, ["Codex"] = [] }
    };

    private static AccountFailover Failover(FakeSessions sessions, FakeNotifier notifier, int? defaultSeconds = null,
        FakeMessages? messages = null, FakeDelivery? delivery = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentHub:AccountExhaustedDefaultSeconds"] = defaultSeconds?.ToString()
        }).Build();
        return new AccountFailover(sessions, [notifier], configuration, NullLogger<AccountFailover>.Instance,
            messages, delivery, () => Now);
    }

    // ------------------------------------------------------------------ marking

    [Fact]
    public async Task MarksTheResolvedAccountOfAnUnpinnedSession_ForTheDefaultHour()
    {
        var sessions = Sessions(Work);

        var outcome = await Failover(sessions, new FakeNotifier()).HandleAsync(Session(),
            new AccountExhaustedReport("output", Detail: "You've hit your usage limit"), CancellationToken.None);

        Assert.Equal("work0001", outcome.AccountId);
        Assert.Equal(Now.AddHours(1), outcome.ExhaustedUntil);
        var marked = Assert.Single(sessions.Marked);
        Assert.Equal(("alice", AgentKind.Claude, "work0001", Now.AddHours(1)), (marked.Owner, marked.Agent, marked.Id, marked.Until));
        Assert.Equal("output You've hit your usage limit", marked.Reason);
    }

    [Fact]
    public async Task ThePinWinsOverTheResolvedAccount_AndTheReportsResetTimeOverTheDefault()
    {
        var sessions = Sessions(Work, Home);

        var outcome = await Failover(sessions, new FakeNotifier()).HandleAsync(Session(credentialId: "home0002"),
            new AccountExhaustedReport("mod", "five_hour", 100, Now.AddHours(3)), CancellationToken.None);

        Assert.Equal("home0002", outcome.AccountId);
        Assert.Equal(Now.AddHours(3), outcome.ExhaustedUntil);
        Assert.Equal("mod five_hour 100%", Assert.Single(sessions.Marked).Reason);
    }

    [Fact]
    public void AResetTimeIsBounded_PastIsTheDefault_FarFutureIsClamped()
    {
        var failover = Failover(Sessions(), new FakeNotifier(), defaultSeconds: 600);

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
        var sessions = Sessions(Work, Home);
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(resolved: resolved, auth: auth),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Ignored, outcome.Action);
        Assert.Empty(sessions.Marked);
        Assert.Empty(sessions.Switched);
        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_IsIgnoredWithoutANotification()
    {
        var sessions = new FakeSessions { Missing = true, Info = Running() };
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Ignored, outcome.Action);
        Assert.Empty(notifier.Events);
    }

    // ------------------------------------------------------------------ switching

    [Fact]
    public async Task SwitchesARunningSessionToTheNextAccount_TellsTheSession_AndNotifies()
    {
        var sessions = Sessions(Work, Home, Spare);
        var notifier = new FakeNotifier();
        var messages = new FakeMessages();
        var delivery = new FakeDelivery();

        var outcome = await Failover(sessions, notifier, messages: messages, delivery: delivery).HandleAsync(Session(),
            new AccountExhaustedReport("mod", "five_hour", 100, Now.AddHours(2)), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Switched, outcome.Action);
        // Away from the default: the never-used account before the one used three days ago.
        Assert.Equal("spare003", outcome.SwitchedTo);
        var switched = Assert.Single(sessions.Switched);
        Assert.Equal(("alice", "s1", "spare003"), (switched.Owner, switched.Id, switched.CredentialId));
        Assert.Equal("Switched to account \"Spare\" because \"Work\" hit its usage limit (resets 14:00 UTC)", switched.Reason);

        // The session hears it as a priority message from outside the fleet, stored then pushed.
        var message = Assert.Single(messages.Added);
        Assert.Null(message.FromSessionId);
        Assert.Equal("s1", message.ToSessionId);
        Assert.True(message.Priority);
        Assert.False(message.Interrupt);
        Assert.StartsWith("Switched to account \"Spare\"", message.Body);
        Assert.Equal(message.Id, Assert.Single(delivery.Pushed).Id);

        var (_, ev, text) = Assert.Single(notifier.Events);
        Assert.Equal(AccountFailover.SwitchedEvent, ev);
        Assert.Contains("\"Spare\"", text);
    }

    [Fact]
    public async Task WithTheSwitchOff_OnlyMarksAndSaysSo()
    {
        var sessions = Sessions(Work, Home);
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(failover: "off"),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Marked, outcome.Action);
        Assert.Empty(sessions.Switched);
        var (_, ev, text) = Assert.Single(notifier.Events);
        Assert.Equal(AccountFailover.ExhaustedEvent, ev);
        Assert.Contains("automatic switching is off", text);
    }

    [Theory]
    [InlineData("Succeeded", "10.0.0.5")]
    [InlineData("Running", null)]
    public async Task ASessionThatIsNotRunning_IsMarkedOnly(string phase, string? podIp)
    {
        var sessions = Sessions(Work, Home);
        sessions.Info = Running(phase, podIp);

        var outcome = await Failover(sessions, new FakeNotifier()).HandleAsync(Session(),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.Marked, outcome.Action);
        Assert.Empty(sessions.Switched);
    }

    [Fact]
    public async Task WithoutAnAlternative_TheSessionStays_AndTheNotifierSaysSo()
    {
        var exhaustedHome = Home with { IsExhausted = true, ExhaustedUntil = Now.AddHours(1) };
        var sessions = Sessions(Work, exhaustedHome);
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.NoAlternative, outcome.Action);
        Assert.Empty(sessions.Switched);
        var (_, ev, text) = Assert.Single(notifier.Events);
        Assert.Equal(AccountFailover.ExhaustedEvent, ev);
        Assert.Contains("no other account is available", text);
    }

    [Fact]
    public async Task AFailedSwitch_KeepsTheMark_AndReportsTheFailure()
    {
        var sessions = Sessions(Work, Home);
        sessions.SwitchThrows = new HttpRequestException("pod refused");
        var notifier = new FakeNotifier();

        var outcome = await Failover(sessions, notifier).HandleAsync(Session(),
            new AccountExhaustedReport("output"), CancellationToken.None);

        Assert.Equal(AccountFailoverOutcome.SwitchFailed, outcome.Action);
        Assert.Single(sessions.Marked);
        var (_, ev, text) = Assert.Single(notifier.Events);
        Assert.Equal(AccountFailover.ExhaustedEvent, ev);
        Assert.Contains("pod refused", text);
    }

    [Fact]
    public void PickAlternative_DefaultFirst_ThenLeastRecentlyUsed_NeverCurrentOrExhausted()
    {
        Assert.Equal("work0001", AccountFailover.PickAlternative([Work, Home, Spare], "spare003")!.Id);
        Assert.Equal("spare003", AccountFailover.PickAlternative([Work, Home, Spare], "work0001")!.Id);
        Assert.Equal("home0002", AccountFailover.PickAlternative([Work, Home, Spare with { IsExhausted = true }], "work0001")!.Id);
        Assert.Null(AccountFailover.PickAlternative([Work], "work0001"));
    }

    [Fact]
    public void FailoverMode_NormalizesTheTwoWords_AndRejectsTheRest()
    {
        Assert.Null(AccountFailoverMode.Normalize(null));
        Assert.Null(AccountFailoverMode.Normalize(""));
        Assert.Null(AccountFailoverMode.Normalize(" Auto "));
        Assert.Equal("off", AccountFailoverMode.Normalize("OFF"));
        Assert.Throws<ArgumentException>(() => AccountFailoverMode.Normalize("maybe"));
        Assert.Equal("auto", AccountFailoverMode.Display(null));
        Assert.Equal("off", AccountFailoverMode.Display("off"));
        Assert.True(AccountFailoverMode.IsOff("off"));
        Assert.False(AccountFailoverMode.IsOff(null));
    }

    [Fact]
    public void Duplicate_CopiesTheFailoverSetting_UnlessOverridden()
    {
        var source = Session(failover: "off");
        Assert.Equal("off", SessionDuplication.CopyableRequest(source, new("copy", null, false)).AccountFailover);
        Assert.Equal("auto", SessionDuplication.CopyableRequest(source, new("copy", null, false, AccountFailover: "auto")).AccountFailover);
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

    private sealed class FakeNotifier : INotifier
    {
        public List<(SessionRecord Session, string Event, string Message)> Events { get; } = [];
        public Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
        {
            Events.Add((s, eventType, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMessages : ISessionMessageStore
    {
        public List<SessionMessageRecord> Added { get; } = [];
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAsync(SessionMessageRecord message, CancellationToken ct = default) { Added.Add(message); return Task.CompletedTask; }
        public Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionMessageRecord>>([]);
        public Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionMessageRecord>>([]);
        public Task MarkDeliveredAsync(string id, string via, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeDelivery : ISessionMessageDelivery
    {
        public List<SessionMessageRecord> Pushed { get; } = [];
        public Task<MessageDelivery> TryInjectAsync(SessionInfo target, SessionMessageRecord message, string? fromTitle, CancellationToken ct)
        {
            Pushed.Add(message);
            return Task.FromResult(new MessageDelivery(MessageDeliveryVia.Mod));
        }
    }

    private sealed class FakeSessions : ISessionService
    {
        public bool Missing { get; init; }
        public SessionInfo? Info { get; set; }
        public Exception? SwitchThrows { get; set; }
        public IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> Accounts { get; init; } =
            new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>();
        public List<(string Owner, AgentKind Agent, string Id, DateTime Until, string? Reason)> Marked { get; } = [];
        public List<(string Owner, string Id, string CredentialId, string? Reason)> Switched { get; } = [];

        public Task<ProviderAccountInfo?> MarkProviderAccountExhaustedAsync(string owner, AgentKind agent, string id,
            DateTime until, string? reason, CancellationToken ct = default)
        {
            if (Missing) return Task.FromResult<ProviderAccountInfo?>(null);
            Marked.Add((owner, agent, id, until, reason));
            var label = Accounts.TryGetValue(agent.ToString(), out var list) ? list.FirstOrDefault(a => a.Id == id)?.Label ?? id : id;
            return Task.FromResult<ProviderAccountInfo?>(new(id, label, null, null, Now, null, true, until, reason, true));
        }
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult(Info);
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(Accounts);
        public Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId, string? reason, CancellationToken ct = default)
        {
            if (SwitchThrows is not null) throw SwitchThrows;
            Switched.Add((owner, id, credentialId, reason));
            return Task.FromResult(Info! with { CredentialId = credentialId });
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
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
