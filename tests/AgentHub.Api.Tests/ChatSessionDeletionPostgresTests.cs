using System.Net;
using System.Reflection;
using System.Text.Json;
using AgentHub.Api.Chat;
using AgentHub.Api.Chat.Signal;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>#365: a deleted session leaves no chat binding behind, on any platform.</summary>
public class ChatSessionDeletionPostgresTests
{
    private static Task AddSession(PostgresPurgeDatabase db, string id, string owner = "alice", int minutesOld = 0)
        => db.Sessions.UpsertAsync(new SessionRecord
        {
            Id = id, Owner = owner, Title = "Title " + id, CallbackToken = "cb-" + id,
            LastActivityAt = DateTime.UtcNow.AddMinutes(-minutesOld)
        });

    [PostgreSqlFact]
    public async Task RemoveSession_DropsEveryPlatformsBinding_AndLeavesAMarker()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        await AddSession(db, "gone");
        await AddSession(db, "kept");
        await db.Bindings.UpsertAsync(new("telegram", "gone", "alice", "chat-1", "77", null, false));
        await db.Bindings.UpsertAsync(new("signal", "gone", "alice", "+100", null, null, false));
        await db.Bindings.UpsertAsync(new("telegram", "kept", "alice", "chat-1", null, null, false));
        await db.Bindings.SetActiveAsync("telegram", "chat-1", "gone");
        await db.Bindings.RecordMessageAsync("telegram", "chat-1", "1", "gone");
        await db.Bindings.RecordMessageAsync("signal", "+100", "2", "gone");
        await db.Bindings.RecordMessageAsync("telegram", "chat-1", "3", "kept");

        await db.Bindings.RemoveSessionAsync("gone", "Release notes");
        // A delete retried after a later step failed runs the cleanup again.
        await db.Bindings.RemoveSessionAsync("gone", "Release notes");

        Assert.Null(await db.Bindings.GetAsync("telegram", "gone"));
        Assert.Null(await db.Bindings.GetAsync("signal", "gone"));
        Assert.Null(await db.Bindings.GetActiveAsync("telegram", "chat-1"));
        Assert.Null(await db.Bindings.GetSessionByMessageAsync("telegram", "chat-1", "1"));
        Assert.Null(await db.Bindings.GetSessionByMessageAsync("signal", "+100", "2"));
        Assert.Equal("kept", await db.Bindings.GetSessionByMessageAsync("telegram", "chat-1", "3"));
        Assert.NotNull(await db.Bindings.GetAsync("telegram", "kept"));

        var active = await db.Bindings.GetDeletedActiveAsync("telegram", "chat-1");
        Assert.Equal(new DeletedChatSession("telegram", "gone", "alice", "chat-1", "77", "Release notes", true), active);
        Assert.Equal("gone", (await db.Bindings.GetDeletedByThreadAsync("telegram", "chat-1", "77"))?.SessionId);
        Assert.False((await db.Bindings.GetDeletedAsync("signal", "gone"))!.WasActive);
    }

    [PostgreSqlFact]
    public async Task ChoosingAnotherSession_RetiresTheDeletedAnswer()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        await AddSession(db, "gone");
        await AddSession(db, "next");
        await db.Bindings.UpsertAsync(new("telegram", "gone", "alice", "chat-1", null, null, false));
        await db.Bindings.UpsertAsync(new("telegram", "next", "alice", "chat-1", null, null, false));
        await db.Bindings.SetActiveAsync("telegram", "chat-1", "gone");
        await db.Bindings.RemoveSessionAsync("gone", null);
        Assert.NotNull(await db.Bindings.GetDeletedActiveAsync("telegram", "chat-1"));

        await db.Bindings.SetActiveAsync("telegram", "chat-1", "next");

        Assert.Null(await db.Bindings.GetDeletedActiveAsync("telegram", "chat-1"));
        Assert.NotNull(await db.Bindings.GetDeletedAsync("telegram", "gone"));
    }

    [PostgreSqlFact]
    public async Task Prune_RemovesBindingsOfSessionsThatNoLongerExist()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        await AddSession(db, "live");
        await db.Bindings.UpsertAsync(new("telegram", "live", "alice", "chat-1", null, null, false));
        // What deletes before #365 left behind, one of them the chat's active session.
        await db.Bindings.UpsertAsync(new("telegram", "e8d5", "alice", "chat-1", null, null, false));
        await db.Bindings.UpsertAsync(new("signal", "f00d", "alice", "+100", null, null, false));
        await db.Bindings.SetActiveAsync("telegram", "chat-1", "e8d5");
        await db.Bindings.RecordMessageAsync("telegram", "chat-1", "1", "e8d5");
        await db.Bindings.RecordMessageAsync("telegram", "chat-1", "2", "live");

        Assert.Equal(2, await db.Bindings.PruneAsync());
        Assert.Equal(0, await db.Bindings.PruneAsync());

        Assert.Equal(new[] { "live" }, (await db.Bindings.ListByChatAsync("telegram", "chat-1")).Select(b => b.SessionId));
        Assert.Null(await db.Bindings.GetAsync("signal", "f00d"));
        Assert.Null(await db.Bindings.GetSessionByMessageAsync("telegram", "chat-1", "1"));
        Assert.Equal("live", await db.Bindings.GetSessionByMessageAsync("telegram", "chat-1", "2"));
        Assert.Equal("e8d5", (await db.Bindings.GetDeletedActiveAsync("telegram", "chat-1"))?.SessionId);
    }

    [PostgreSqlFact]
    public async Task Prune_ForgetsMarkersAndReplyRoutingAfterThirtyDays()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        await AddSession(db, "live");
        await db.Bindings.UpsertAsync(new("telegram", "old", "alice", "chat-1", null, null, false));
        await db.Bindings.UpsertAsync(new("telegram", "new", "alice", "chat-1", null, null, false));
        await db.Bindings.RemoveSessionAsync("old", null);
        await db.Bindings.RemoveSessionAsync("new", null);
        await db.Bindings.RecordMessageAsync("telegram", "chat-1", "1", "live");
        await db.ExecAsync("""
            UPDATE chat_deleted_sessions SET deleted_at = now() - interval '31 days' WHERE session_id = 'old';
            UPDATE chat_messages SET created_at = now() - interval '31 days';
            """);

        await db.Bindings.PruneAsync();

        Assert.Null(await db.Bindings.GetDeletedAsync("telegram", "old"));
        Assert.NotNull(await db.Bindings.GetDeletedAsync("telegram", "new"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_messages"));
    }

    [PostgreSqlFact]
    public async Task AccountPurge_RemovesTheMarkersAndBindings()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        await db.Bindings.UpsertAsync(new("signal", "live", "alice", "+100", null, null, true));
        await db.Bindings.UpsertAsync(new("telegram", "gone", "alice", "chat-1", null, null, true));
        await db.Bindings.UpsertAsync(new("telegram", "other", "bob", "chat-2", null, null, true));
        await db.Bindings.RemoveSessionAsync("gone", "secret title");
        await db.Bindings.RemoveSessionAsync("other", null);

        await db.CreatePurgeStore().PurgeAsync("alice", ["gone", "live"]);

        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_deleted_sessions WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_session_bindings WHERE owner = 'alice'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM chat_deleted_sessions WHERE owner = 'bob'"));
    }

    // ---------------------------------------------------------------------- Signal

    [PostgreSqlFact]
    public async Task Signal_SessionsListsOnlyExistingSessions_AndAnswersForTheDeletedActiveOne()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        var signal = await SignalRig.CreateAsync(db);
        await AddSession(db, "aaaa1111", minutesOld: 60);
        await AddSession(db, "bbbb2222", minutesOld: 1);
        foreach (var id in new[] { "aaaa1111", "bbbb2222", "dead3333" })
            await db.Bindings.UpsertAsync(new("signal", id, "alice", SignalRig.Number, null, null, false));
        await db.Bindings.SetActiveAsync("signal", SignalRig.Number, "aaaa1111");

        await signal.SendAsync("!sessions");
        Assert.Equal("Title bbbb2222 (#bbbb) · Pending\nTitle aaaa1111 (#aaaa) · Pending · active", signal.Sent.Last());

        await db.Bindings.RemoveSessionAsync("aaaa1111", "Title aaaa1111");
        await db.Sessions.DeleteAsync("aaaa1111");
        await signal.SendAsync("please continue");
        Assert.Equal("🗑 The active session Title aaaa1111 (#aaaa) was deleted. !sessions lists the remaining ones, !use <tag> picks one.",
            signal.Sent.Last());
        await signal.SendAsync("!use dead");
        Assert.Equal("No session matches that tag.", signal.Sent.Last());
        await signal.SendAsync("!sessions");
        Assert.Equal("Title bbbb2222 (#bbbb) · Pending", signal.Sent.Last());
    }

    [PostgreSqlFact]
    public async Task Signal_ExpiryNoticeAfterTheDeleteStillReachesTheChat()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        var signal = await SignalRig.CreateAsync(db);
        await AddSession(db, "exp1");
        await db.Bindings.UpsertAsync(new("signal", "exp1", "alice", SignalRig.Number, null, null, false));
        await db.Bindings.RemoveSessionAsync("exp1", "Nightly");

        await signal.Notifier.NotifyAsync(new SessionRecord { Id = "exp1", Owner = "alice", Title = "Nightly", CallbackToken = "x" },
            SessionExpirySweepService.NotifierEvent, "deleted automatically");

        Assert.Equal("🏁 session-expired — deleted automatically", signal.Sent.Single());
        // No reply-routing row for a session that no longer exists.
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_messages"));
    }

    private sealed class SignalRig
    {
        public const string Number = "+15550100";
        /// <summary>Every message text the client sent, in order.</summary>
        public required List<string> Sent { get; init; }
        public required SignalReceiveService Receiver { get; init; }
        public required SignalNotifier Notifier { get; init; }

        public static async Task<SignalRig> CreateAsync(PostgresPurgeDatabase db)
        {
            await db.Users.RecordLoginAsync("alice", null, null);
            await db.Users.SetSignalNumberAsync("alice", Number);
            await db.Users.SetSignalVerifiedAsync("alice", true);
            await db.Users.SetSignalEnabledAsync("alice", true);
            var permissions = new PermissionStore(db.Config);
            await permissions.InitializeAsync();
            var opts = new SignalOptions { Enabled = true, ApiUrl = "http://signal.example.com", Number = "+15550000" };
            var sent = new List<string>();
            var client = new SignalClient(new RecordingFactory(sent), opts, NullLogger<SignalClient>.Instance);
            var sessions = DispatchProxy.Create<ISessionService, StoredSessions>();
            ((StoredSessions)sessions).Store = db.Sessions;
            var indicator = new WorkingIndicator();
            var receiver = new SignalReceiveService(opts, client, db.Bindings, db.Users, permissions, sessions,
                new ChatRepoService(null!, NullLogger<ChatRepoService>.Instance), indicator, null!, db.Config,
                NullLogger<SignalReceiveService>.Instance);
            var notifier = new SignalNotifier(opts, client, db.Bindings, db.Users, indicator, db.Config, NullLogger<SignalNotifier>.Instance);
            return new SignalRig { Sent = sent, Receiver = receiver, Notifier = notifier };
        }

        public async Task SendAsync(string text)
        {
            var json = JsonSerializer.Serialize(new { envelope = new { sourceNumber = Number, timestamp = 1, dataMessage = new { message = text } } });
            await (Task)typeof(SignalReceiveService).GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Receiver, [SignalEnvelope.Parse(json)!, CancellationToken.None])!;
        }
    }

    /// <summary>Session lookups answered from the real session table: a deleted row is a deleted session.</summary>
    public class StoredSessions : DispatchProxy
    {
        public PostgresSessionStore Store = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(ISessionService.ListSessionsAsync) => ListAsync((string)args![0]!),
            nameof(ISessionService.GetSessionAsync) => GetAsync((string)args![0]!, (string)args[1]!),
            _ => throw new NotSupportedException(method.Name)
        };

        private async Task<IReadOnlyList<SessionInfo>> ListAsync(string owner)
            => (await Store.ListAsync(owner)).Select(Info).ToList();

        private async Task<SessionInfo?> GetAsync(string owner, string id)
            => await Store.GetAsync(owner, id) is { } r ? Info(r) : null;

        private static SessionInfo Info(SessionRecord r) => new()
        {
            Id = r.Id, Owner = r.Owner, Title = r.Title, Mode = r.Mode, Phase = "Pending", LastActivityAt = r.LastActivityAt
        };
    }

    private sealed class RecordingFactory(List<string> sent) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Recorder(sent));

        private sealed class Recorder(List<string> sent) : HttpMessageHandler
        {
            private long _timestamp = 1000;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                lock (sent) sent.Add(body.RootElement.GetProperty("message").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"{{\"timestamp\":{Interlocked.Increment(ref _timestamp)}}}") };
            }
        }
    }
}
