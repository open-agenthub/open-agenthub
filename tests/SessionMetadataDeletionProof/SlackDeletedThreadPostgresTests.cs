using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using AgentHub.Api.Chat;
using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Licensing;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class SlackDeletedThreadPostgresTests
{
    [PostgreSqlFact]
    public async Task RepliesAndStatusInDeletedThread_GetDeletedAnswer_WithoutCallingSessionOrPod()
    {
        await using var db = await SessionMetadataDeletionPostgresTests.CreateDatabaseAsync();
        var threads = new SlackThreadStore(db.Configuration);
        await threads.InitializeAsync();
        await db.AddSessionAsync("owner", "deleted");
        await threads.UpsertAsync(new("deleted", "owner", "channel", "thread"));
        await db.Sessions.DeleteAsync("deleted");
        Assert.Null(await threads.GetBySessionAsync("deleted"));

        var replies = new List<JsonElement>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/api/chat.postMessage", async (HttpRequest request) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            replies.Add(body.RootElement.Clone());
            return Results.Json(new { ok = true, ts = "reply" });
        });
        await app.StartAsync();
        using var http = new HttpClient(new LoopbackHandler(new Uri(app.Urls.Single())));
        var options = new SlackOptions { Enabled = true, BotToken = "proof-token" };
        var client = new SlackClient(RuntimeProofProxy.Make<IHttpClientFactory>((_, _) => http), options,
            NullLogger<SlackClient>.Instance);
        var sessionCalls = 0;
        var podCalls = 0;
        var sessions = RuntimeProofProxy.Make<ISessionService>((_, _) =>
        {
            sessionCalls++;
            throw new InvalidOperationException("A deleted thread must not consult or resume a session.");
        });
        var pods = RuntimeProofProxy.Make<IAgentPodRequests>((_, _) =>
        {
            podCalls++;
            throw new InvalidOperationException("A deleted thread must not send terminal input.");
        });
        using var receiver = new SlackSocketModeService(options, new EnabledLicense(), client, threads,
            new PermissionStore(db.Configuration), sessions, new UserDirectory(db.Configuration), null!,
            new WorkingIndicator(), pods, db.Configuration, NullLogger<SlackSocketModeService>.Instance);
        foreach (var text in new[] { "hello", "!status" })
        {
            await DispatchAsync(receiver, "channel", text);
            var reply = replies[^1];
            Assert.Equal("channel", reply.GetProperty("channel").GetString());
            Assert.Equal("thread", reply.GetProperty("thread_ts").GetString());
            Assert.Contains("This session was deleted", reply.GetProperty("text").GetString());
            Assert.DoesNotContain("not running", reply.GetProperty("text").GetString());
        }
        Assert.Equal(2, replies.Count);
        await DispatchAsync(receiver, "other-channel", "hello");
        Assert.Equal(2, replies.Count);
        Assert.Equal(0, sessionCalls);
        Assert.Equal(0, podCalls);
        await db.ExecuteAsync("UPDATE chat_deleted_sessions SET deleted_at = now() - interval '30 days'");
        await DispatchAsync(receiver, "channel", "hello");
        Assert.Equal(2, replies.Count);
    }

    [PostgreSqlFact]
    public async Task RetentionWorker_PrunesWithoutAnEnabledRelay_AndAccountPurgeDeletesImmediately()
    {
        await using var db = await SessionMetadataDeletionPostgresTests.CreateDatabaseAsync();
        var threads = new SlackThreadStore(db.Configuration);
        await threads.InitializeAsync();
        foreach (var id in new[] { "old", "recent", "other" })
        {
            await db.AddSessionAsync(id == "other" ? "other" : "owner", id);
            await threads.UpsertAsync(new(id, id == "other" ? "other" : "owner", "channel", id));
            await db.Sessions.DeleteAsync(id);
        }
        await db.ExecuteAsync("UPDATE chat_deleted_sessions SET deleted_at = now() - interval '29 days 1 hour' WHERE session_id = 'old'");
        using (var worker = new SlackThreadPruneService(threads, NullLogger<SlackThreadPruneService>.Instance))
        {
            await worker.StartAsync(default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await threads.IsDeletedThreadAsync("channel", "old"))
                await Task.Delay(10, deadline.Token);
            await worker.StopAsync(default);
        }
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM chat_deleted_sessions WHERE session_id = 'old'"));
        Assert.True(await threads.IsDeletedThreadAsync("channel", "recent"));
        await new AccountPurgeStore(db.Configuration, NullLogger<AccountPurgeStore>.Instance).PurgeAsync("owner", []);
        Assert.False(await threads.IsDeletedThreadAsync("channel", "recent"));
        Assert.True(await threads.IsDeletedThreadAsync("channel", "other"));
    }

    [PostgreSqlFact]
    public async Task AccountPurge_RemovesMarkersCreatedByItsFinalRegistryCascade()
    {
        await using var db = await SessionMetadataDeletionPostgresTests.CreateDatabaseAsync();
        var threads = new SlackThreadStore(db.Configuration);
        await threads.InitializeAsync();
        await db.AddSessionAsync("owner", "owned");
        // A legacy owner mismatch leaves this row for the final registry cascade rather than
        // the owner-keyed metadata delete. Its newly created marker must still be purged.
        await threads.UpsertAsync(new("owned", "legacy-owner", "channel", "owned"));
        await new AccountPurgeStore(db.Configuration, NullLogger<AccountPurgeStore>.Instance).PurgeAsync("owner", ["owned"]);
        Assert.Null(await threads.GetBySessionAsync("owned"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM chat_deleted_sessions WHERE session_id = 'owned'"));
    }

    private static async Task DispatchAsync(SlackSocketModeService receiver, string channel, string text)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            payload = new { @event = new { type = "message", channel, thread_ts = "thread", text } }
        }));
        var handler = typeof(SlackSocketModeService).GetMethod("HandleEventCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)handler.Invoke(receiver, [document.RootElement, CancellationToken.None])!;
    }

    private sealed class LoopbackHandler(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri(endpoint, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }

    private sealed class EnabledLicense : IEnterpriseLicense
    {
        public LicenseStatus Status { get; } = new() { Valid = true };
        public bool Enabled => true;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
