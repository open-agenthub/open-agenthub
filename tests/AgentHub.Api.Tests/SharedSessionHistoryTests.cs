using System.Net;
using System.Security.Claims;
using System.Text;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #427: everyone a session is shared with reads the history the owner reads, through
/// <see cref="ISharedSessionHistory"/>. The Postgres tests use the real share store and access
/// service, the real Postgres transcript copy (capped at <see cref="ScrollbackLimits.MaxChars"/>)
/// and the real <see cref="LiveTranscriptReader"/> against a stand-in pod that answers
/// /agenthub/transcript the way common/server.js does.
/// </summary>
public class SharedSessionHistoryTests
{
    private const string Owner = "owner-a";
    private const string SessionId = "session-a";
    private const string CallbackToken = "callback-token-session-a-7f3c9e";
    private const string McpCredential = "mcp-owner-credential-5b1d2a";
    private const string McpEnvValue = "mcp-env-value-9e8d7c";
    private const string FirstReview = "Review 1 of today: approved the parser change";

    private static string McpConfig =>
        "{\"mcpServers\":{\"tracker\":{\"type\":\"http\",\"url\":\"https://tracker.example.com/mcp\",\"headers\":{\"Authorization\":\"Bearer " + McpCredential + "\"}},\"local\":{\"command\":\"node\",\"args\":[\"server.mjs\"],\"env\":{\"TRACKER_KEY\":\"" + McpEnvValue + "\"}}}}";

    private static string Line(string role, string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = role, message = new { role, content = text } }) + "\n";

    // Long enough that the Postgres copy, which keeps only the last ScrollbackLimits.MaxChars,
    // no longer contains the first turns — what a reader got once the S3 copy had frozen.
    private static string LongTranscript()
    {
        var builder = new StringBuilder();
        builder.Append(Line("user", FirstReview));
        var filler = new string('x', 4_000);
        for (var i = 2; i <= 400; i++)
            builder.Append(Line("assistant", $"Review {i}: {filler}"));
        builder.Append(Line("assistant",
            $"env shows AGENTHUB_CALLBACK_TOKEN={CallbackToken}; curl -H 'Authorization: Bearer {McpCredential}'; TRACKER_KEY={McpEnvValue}"));
        return builder.ToString();
    }

    [PostgreSqlFact]
    public async Task EverySharedReader_GetsHistoryOlderThanTheStoredTail_Sanitized()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var transcript = LongTranscript();
        var fixture = await Fixture.CreateAsync(database, transcript);

        // The stored copy really lacks the first turn, so it alone cannot satisfy the reader —
        // and the path a link took before #427 (stored copy only) shows the gap.
        var stored = await database.Sessions.GetTranscriptAsync(SessionId);
        Assert.DoesNotContain(FirstReview, stored);
        var before = await SessionTranscripts.PageAsync(fixture.Sessions, Owner, SessionId, SessionStatus.Running, 0, null, default);
        Assert.NotEqual(FirstReview, before.Entries[0].Text);

        var link = await fixture.Sharing.CreateLinkAsync(Owner, SessionId, ShareRole.Viewer, DateTime.UtcNow.AddHours(1));
        var linkToken = link.Url[(link.Url.LastIndexOf('/') + 1)..];

        var pages = new Dictionary<string, Func<int?, Task<ConversationPage>>>
        {
            ["named viewer"] = offset => fixture.NamedAsync("viewer", SessionId, offset),
            ["named collaborator"] = offset => fixture.NamedAsync("collaborator", SessionId, offset),
            ["link"] = offset => fixture.LinkAsync(linkToken, offset),
            ["remote token"] = offset => fixture.RemoteAsync("viewer", SessionId, offset),
        };
        foreach (var (reader, read) in pages)
        {
            var first = await read(0);
            Assert.True(first.Entries.Count > 0, reader);
            Assert.Equal(FirstReview, first.Entries[0].Text);
            Assert.Equal(ConversationPage.NativeSource, first.Source);
            Assert.True(first.Entries.Count <= SharedSessionHistory.MaxEntries, reader);
            Assert.True(first.Entries.Sum(e => e.Text.Length) <= SharedSessionHistory.MaxPageChars, reader);
            Assert.True(first.Truncated, reader);

            var recent = await read(-1);
            var last = recent.Entries[^1].Text;
            Assert.DoesNotContain(CallbackToken, last);
            Assert.DoesNotContain(McpCredential, last);
            Assert.DoesNotContain(McpEnvValue, last);
            Assert.Contains($"AGENTHUB_CALLBACK_TOKEN={SharedSessionHistory.Redacted}", last);
            Assert.Equal(recent.Length, recent.NextOffset);
        }

        // The owner's own page is unchanged: the owner may read their own token.
        var owners = await fixture.NamedAsync(Owner, SessionId, -1);
        Assert.Contains(CallbackToken, owners.Entries[^1].Text);
    }

    [PostgreSqlFact]
    public async Task InheritedChildSession_IsReadThroughTheParentsShare()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var fixture = await Fixture.CreateAsync(database, LongTranscript());
        await database.UpsertSessionAsync(new SessionRecord
        {
            Id = "child-a", Owner = Owner, Title = "child", Mode = SessionMode.Interactive,
            ParentSessionId = SessionId, Status = SessionStatus.Paused, CallbackToken = "callback-token-child-a-1234"
        });
        await database.Sessions.SetTranscriptAsync("child-a", Line("user", "child review") + Line("assistant", "callback-token-child-a-1234"));

        var page = await fixture.NamedAsync("viewer", "child-a", 0);

        Assert.Equal("child review", page.Entries[0].Text);
        Assert.Equal(SharedSessionHistory.Redacted, page.Entries[1].Text);
    }

    [PostgreSqlFact]
    public async Task RevokedShares_DeletedLinks_AndExpiredLinks_LoseTheHistory()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var fixture = await Fixture.CreateAsync(database, LongTranscript());
        var link = await fixture.Sharing.CreateLinkAsync(Owner, SessionId, ShareRole.Collaborator, DateTime.UtcNow.AddHours(1));
        var token = link.Url[(link.Url.LastIndexOf('/') + 1)..];
        var expiring = await fixture.Sharing.CreateLinkAsync(Owner, SessionId, ShareRole.Viewer, DateTime.UtcNow.AddHours(1));
        var expiringToken = expiring.Url[(expiring.Url.LastIndexOf('/') + 1)..];
        Assert.Equal(FirstReview, (await fixture.LinkAsync(token, 0)).Entries[0].Text);
        Assert.Equal(FirstReview, (await fixture.LinkAsync(expiringToken, 0)).Entries[0].Text);
        Assert.Equal(FirstReview, (await fixture.NamedAsync("viewer", SessionId, 0)).Entries[0].Text);

        await fixture.Sharing.UnshareUserAsync(Owner, SessionId, "viewer");
        await fixture.Sharing.DeleteLinkAsync(Owner, SessionId, link.Link.Id);
        await database.ExecuteAsync(
            "UPDATE session_share_links SET expires_at = now() - interval '1 minute' WHERE id = @id",
            new NpgsqlParameter("id", expiring.Link.Id));

        Assert.IsType<NotFoundResult>(await fixture.NamedResultAsync("viewer", SessionId, 0));
        Assert.IsType<NotFoundResult>(await fixture.RemoteResultAsync("viewer", SessionId, 0));
        Assert.IsType<NotFoundResult>(await fixture.LinkResultAsync(token, 0));
        Assert.IsType<NotFoundResult>(await fixture.LinkResultAsync(expiringToken, 0));
        Assert.IsType<NotFoundResult>(await fixture.LinkTranscriptAsync(expiringToken));
        // The other named share is untouched.
        Assert.Equal(FirstReview, (await fixture.NamedAsync("collaborator", SessionId, 0)).Entries[0].Text);
    }

    [Fact]
    public void Sanitize_WithholdsTheCallbackTokenAndMcpCredentials_AndKeepsShortValues()
    {
        var record = new SessionRecord
        {
            Id = SessionId, Owner = Owner, CallbackToken = CallbackToken, McpConfigJson =
                """{"mcpServers":{"a":{"headers":{"X-Short":"abc","Authorization":"Bearer bearer-value-12345"},"url":"https://example.com/mcp?key=url-key-123456"}}}"""
        };
        var entries = new List<TranscriptEntry>
        {
            new("assistant", $"token {CallbackToken} bearer-value-12345 https://example.com/mcp?key=url-key-123456 abc", null,
                Tool: $"Bash {CallbackToken}")
        };
        var page = SharedSessionHistory.Sanitize(ConversationPage.FromEntries(SessionId, "Stopped", entries, 0, null), record);

        Assert.Equal("token [redacted] [redacted] [redacted] abc", page.Entries[0].Text);
        Assert.Equal("Bash [redacted]", page.Entries[0].Tool);

        var scrollback = SharedSessionHistory.Sanitize(
            ConversationPage.FromScrollback(SessionId, "Stopped", $"$ env\nTOKEN={CallbackToken}\n", 0, null), record);
        Assert.Equal("$ env\nTOKEN=[redacted]\n", scrollback.Text);
        Assert.Equal("TOKEN=[redacted]", SharedSessionHistory.SanitizeText($"TOKEN={CallbackToken}", record));
    }

    [Fact]
    public void Sanitize_MasksStandaloneUrlUserinfoAndQueryValues()
    {
        var record = new SessionRecord { Id = SessionId, Owner = Owner, CallbackToken = CallbackToken,
            McpConfigJson = """
                {"mcpServers":{"custom":{"url":"https://username-434:password%2F434@example.com/mcp?key=query%2Fvalue434&next=query+space434"}}}
                """ };
        foreach (var value in new[] { "username-434", "password%2F434", "password/434",
            "query%2Fvalue434", "query/value434", "query+space434", "query space434" })
        {
            Assert.Equal("ordinary [redacted]", SharedSessionHistory.SanitizeText("ordinary " + value, record));
            var page = SharedSessionHistory.Sanitize(ConversationPage.FromEntries(SessionId, "Stopped",
                [new("assistant", value, null, Tool: value)], 0, null), record);
            Assert.Equal("[redacted]", page.Entries[0].Text);
            Assert.Equal("[redacted]", page.Entries[0].Tool);
        }
    }

    [Fact]
    public void Sanitize_BoundsEntriesPerPage_KeepingTheNewestForTheRecentWindow()
    {
        var record = new SessionRecord { Id = SessionId, Owner = Owner, CallbackToken = CallbackToken };
        var entries = Enumerable.Range(0, 2_000).Select(i => new TranscriptEntry("user", "m" + i, null)).ToList();

        var forward = SharedSessionHistory.Sanitize(ConversationPage.FromEntries(SessionId, "Stopped", entries, 0, 5_000), record);
        Assert.Equal(SharedSessionHistory.MaxEntries, forward.Entries.Count);
        Assert.Equal("m0", forward.Entries[0].Text);
        Assert.Equal(SharedSessionHistory.MaxEntries, forward.NextOffset);
        Assert.True(forward.Truncated);

        var recent = SharedSessionHistory.Sanitize(ConversationPage.FromEntries(SessionId, "Stopped", entries, -1, 5_000), record, recentWindow: true);
        Assert.Equal(SharedSessionHistory.MaxEntries, recent.Entries.Count);
        Assert.Equal("m1999", recent.Entries[^1].Text);
        Assert.Equal(2_000 - SharedSessionHistory.MaxEntries, recent.Offset);
        Assert.Equal(2_000, recent.NextOffset);
    }

    [Fact]
    public void Sanitize_BoundsCharactersPerPage_ButAlwaysAdvances()
    {
        var record = new SessionRecord { Id = SessionId, Owner = Owner, CallbackToken = CallbackToken };
        var huge = new string('y', SharedSessionHistory.MaxPageChars + 10);
        var entries = new List<TranscriptEntry> { new("assistant", huge, null), new("assistant", "next", null) };

        var page = SharedSessionHistory.Sanitize(ConversationPage.FromEntries(SessionId, "Stopped", entries, 0, null), record);

        Assert.Single(page.Entries);
        Assert.Equal(1, page.NextOffset);
        Assert.True(page.Truncated);
    }

    /// <summary>Real share store and access service; the session service reads the real
    /// Postgres transcript copy, and a running session's pod is the stand-in below.</summary>
    private sealed class Fixture
    {
        public required PostgresSharingDatabase Database { get; init; }
        public required SessionSharingService Sharing { get; init; }
        public required SessionAccessService Access { get; init; }
        public required StoreBackedSessions Sessions { get; init; }
        public required LiveTranscriptReader Live { get; init; }

        public static async Task<Fixture> CreateAsync(PostgresSharingDatabase database, string transcript)
        {
            await database.UpsertSessionAsync(new SessionRecord
            {
                Id = SessionId, Owner = Owner, Title = "reviews", Mode = SessionMode.Interactive,
                Status = SessionStatus.Running, CallbackToken = CallbackToken, McpConfigJson = McpConfig
            });
            await database.Sessions.SetTranscriptAsync(SessionId, NativeTranscript.TrimToLineCap(transcript, ScrollbackLimits.MaxChars));
            foreach (var user in new[] { "viewer", "collaborator" }) await database.AddUserAsync(user);
            var license = new FakeEnterpriseLicense(true);
            var sharing = new SessionSharingService(database.Shares, license, "https://hub.example.com");
            await sharing.ShareWithUserAsync(Owner, SessionId, "viewer", ShareRole.Viewer);
            await sharing.ShareWithUserAsync(Owner, SessionId, "collaborator", ShareRole.Collaborator);
            var pod = new StandInPod(Encoding.UTF8.GetBytes(transcript));
            return new Fixture
            {
                Database = database,
                Sharing = sharing,
                Access = new SessionAccessService(database.Shares, license),
                Sessions = new StoreBackedSessions(database.Sessions),
                Live = new LiveTranscriptReader(TestPods.Using(new HttpClient(pod), new AlwaysLive()), database.Sessions,
                    new ConfigurationBuilder().Build())
            };
        }

        private SharedSessionHistory History => new(Sessions, Live);

        public async Task<IActionResult> NamedResultAsync(string user, string id, int? offset)
        {
            var controller = new SessionsController(Sessions, null!, Array.Empty<IPermissionPromptEditor>(),
                liveTranscripts: Live, access: Access, sharedHistory: History)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test"))
                    }
                }
            };
            var result = await controller.Conversation(id, offset, null, default);
            return result.Result!;
        }

        public async Task<IActionResult> RemoteResultAsync(string user, string id, int? offset)
        {
            var controller = new RemoteReadController((token, _) => Task.FromResult<RemoteCaller?>(
                    token == "oah_" + user ? new RemoteCaller(user, null) : null),
                Sessions, null!, null!, null!, Access, Live)
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            controller.Request.Headers.Authorization = "Bearer oah_" + user;
            return (await controller.Conversation(id, offset, null, default)).Result!;
        }

        public async Task<IActionResult> LinkResultAsync(string token, int? offset)
            => (await new SharedSessionsController(Access).GetConversation(token, offset, null, History, default)).Result!;

        public Task<IActionResult> LinkTranscriptAsync(string token)
            => new SharedSessionsController(Access).GetTranscript(token, Sessions, default);

        public async Task<ConversationPage> NamedAsync(string user, string id, int? offset)
            => Page(await NamedResultAsync(user, id, offset));

        public async Task<ConversationPage> RemoteAsync(string user, string id, int? offset)
            => Page(await RemoteResultAsync(user, id, offset));

        public async Task<ConversationPage> LinkAsync(string token, int? offset)
            => Page(await LinkResultAsync(token, offset));

        private static ConversationPage Page(IActionResult result)
            => Assert.IsType<ConversationPage>(Assert.IsType<OkObjectResult>(result).Value);
    }

    /// <summary>What KubernetesSessionService does for these reads, minus Kubernetes and S3: the
    /// transcript from the Postgres copy (the S3 copy frozen or absent), a running pod's address.</summary>
    private sealed class StoreBackedSessions(PostgresSessionStore store) : RecordingWebhookSessionService
    {
        public override async Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default)
            => await store.GetAsync(owner, id, ct) is { } rec
                ? new SessionInfo
                {
                    Id = rec.Id, Owner = rec.Owner, Title = rec.Title, Phase = rec.Status, Mode = rec.Mode,
                    Agent = AgentKind.Claude, PodIp = rec.Status == SessionStatus.Running ? "10.0.0.5" : null
                }
                : null;

        public override async Task<IReadOnlyList<TranscriptEntry>?> GetConversationAsync(string owner, string id, CancellationToken ct = default)
        {
            if (await store.GetAsync(owner, id, ct) is null) return null;
            var jsonl = await store.GetTranscriptAsync(id, ct);
            return string.IsNullOrEmpty(jsonl) ? null : NativeTranscript.Parse(jsonl);
        }

        public override async Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default)
            => await store.GetAsync(owner, id, ct) is null ? null : $"$ env\nAGENTHUB_CALLBACK_TOKEN={CallbackToken}\n";
    }

    /// <summary>Answers GET /agenthub/transcript?from=N as common/server.js does: whole lines from
    /// a byte offset, with the file size and the next offset in headers.</summary>
    private sealed class StandInPod(byte[] file) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            if (request.Headers.GetValues("X-Agent-Token").Single() != CallbackToken)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var from = long.Parse(request.RequestUri!.Query.Split('=')[1]);
            var available = from >= file.Length ? [] : file[(int)from..];
            var body = available.Length > 8 * 1024 * 1024 ? available[..(8 * 1024 * 1024)] : available;
            var whole = Array.LastIndexOf(body, (byte)'\n') + 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body[..whole]) };
            response.Headers.Add("X-Transcript-Size", file.Length.ToString());
            response.Headers.Add("X-Transcript-Next", (Math.Min(from, file.Length) + whole).ToString());
            return response;
        }
    }

    private sealed class AlwaysLive : IAgentPodIdentityResolver
    {
        public Task<bool> IsLiveSessionPodAsync(string id, IPAddress ip, CancellationToken ct = default) => Task.FromResult(true);
    }
}
