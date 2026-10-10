using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The sweep that deletes expired sessions: due ones go, the re-read under the lock protects a
/// session that was touched in the meantime, a lock held elsewhere means another replica is on
/// it, one failure does not stop the pass, and the chat integrations hear about it.
/// </summary>
public class SessionExpirySweepServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DeletesDueSessions_ThroughTheServicePath_AndNotifies()
    {
        var store = new FakeStore(Due("a", "alice"), Due("b", "bob"));
        var sessions = new FakeSessions();
        var notifier = new FakeNotifier();
        var sweep = Sweep(store, sessions, new FakeLock(), notifier);

        var deleted = await sweep.SweepOnceAsync(Now, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal([("alice", "a"), ("bob", "b")], sessions.Deleted);
        Assert.Equal(["a", "b"], notifier.Events.Select(e => e.Session.Id));
        Assert.All(notifier.Events, e => Assert.Equal("session-expired", e.Event));
        Assert.Contains("1 h since its last use", notifier.Events[0].Message);
    }

    [Fact]
    public async Task ASessionTouchedAfterTheListing_IsLeftAlone()
    {
        // The listing is a snapshot; the re-read under the lock is what decides.
        var store = new FakeStore(Due("a", "alice"));
        store.OnGetById = id =>
        {
            var fresh = Due(id, "alice");
            fresh.LastActivityAt = Now.AddMinutes(-5);
            return fresh;
        };
        var sessions = new FakeSessions();
        var sweep = Sweep(store, sessions, new FakeLock(), new FakeNotifier());

        Assert.Equal(0, await sweep.SweepOnceAsync(Now, CancellationToken.None));
        Assert.Empty(sessions.Deleted);
    }

    [Fact]
    public async Task ASessionAlreadyGone_IsNotDeletedTwice()
    {
        var store = new FakeStore(Due("a", "alice"));
        store.OnGetById = _ => null;
        var sessions = new FakeSessions();
        var sweep = Sweep(store, sessions, new FakeLock(), new FakeNotifier());

        Assert.Equal(0, await sweep.SweepOnceAsync(Now, CancellationToken.None));
        Assert.Empty(sessions.Deleted);
    }

    [Fact]
    public async Task ALockHeldElsewhere_MeansAnotherReplicaIsOnIt()
    {
        var store = new FakeStore(Due("a", "alice"), Due("b", "bob"));
        var sessions = new FakeSessions();
        var locks = new FakeLock { Held = ["a"] };
        var sweep = Sweep(store, sessions, locks, new FakeNotifier());

        Assert.Equal(1, await sweep.SweepOnceAsync(Now, CancellationToken.None));
        Assert.Equal([("bob", "b")], sessions.Deleted);
        // The lock of the one it did take was released again.
        Assert.Equal(["b"], locks.Released);
    }

    [Fact]
    public async Task OneFailure_DoesNotStopThePass_AndReleasesItsLock()
    {
        var store = new FakeStore(Due("a", "alice"), Due("b", "bob"));
        var sessions = new FakeSessions { FailFor = "a" };
        var locks = new FakeLock();
        var sweep = Sweep(store, sessions, locks, new FakeNotifier());

        Assert.Equal(1, await sweep.SweepOnceAsync(Now, CancellationToken.None));
        Assert.Equal([("alice", "a"), ("bob", "b")], sessions.Attempted);
        Assert.Equal(["a", "b"], locks.Released);
    }

    [Theory]
    [InlineData(300, "5 min")]
    [InlineData(5400, "90 min")]
    [InlineData(7200, "2 h")]
    [InlineData(259200, "3 d")]
    public void Describe_UsesTheLargestWholeUnit(int seconds, string expected)
        => Assert.Equal(expected, SessionExpirySweepService.Describe(seconds));

    // ------------------------------------------------------------------ fixtures

    private static SessionRecord Due(string id, string owner) => new()
    {
        Id = id, Owner = owner, Title = id, Mode = SessionMode.Interactive,
        AgentSessionId = $"agent-{id}", CallbackToken = $"token-{id}",
        AutoDeleteAfterSeconds = 3600, AutoDeleteFrom = SessionExpiry.FromLastActivity,
        CreatedAt = Now.AddDays(-1), LastActivityAt = Now.AddHours(-2)
    };

    private static SessionExpirySweepService Sweep(FakeStore store, FakeSessions sessions, FakeLock locks, FakeNotifier notifier)
        => new(store, sessions, locks, [notifier], NullLogger<SessionExpirySweepService>.Instance);

    private sealed class FakeStore(params SessionRecord[] due) : ISessionStore
    {
        public Func<string, SessionRecord?>? OnGetById { get; set; }

        public Task<IReadOnlyList<SessionRecord>> ListExpiredAsync(DateTime now, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>(due.Take(limit).ToList());
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(OnGetById is { } f ? f(id) : due.FirstOrDefault(r => r.Id == id));

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSessions : ISessionService
    {
        public string? FailFor { get; init; }
        public List<(string Owner, string Id)> Attempted { get; } = [];
        public List<(string Owner, string Id)> Deleted { get; } = [];

        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default)
        {
            Attempted.Add((owner, id));
            if (id == FailFor) throw new InvalidOperationException("cluster unreachable");
            Deleted.Add((owner, id));
            return Task.CompletedTask;
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
    }

    private sealed class FakeLock : ISessionExpiryLock
    {
        public HashSet<string> Held { get; init; } = [];
        public List<string> Released { get; } = [];

        public Task<IAsyncDisposable?> TryAcquireAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(Held.Contains(sessionId) ? null : new Release(this, sessionId));

        private sealed class Release(FakeLock owner, string id) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { owner.Released.Add(id); return ValueTask.CompletedTask; }
        }
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
}
