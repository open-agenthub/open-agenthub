using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class RemoteReadHttpPostgresTests
{
    public class SessionReads : DispatchProxy
    {
        public ISessionStore Store = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "GetSessionAsync" => Get((string)args![0]!, (string)args[1]!),
            "GetConversationAsync" => Task.FromResult<IReadOnlyList<TranscriptEntry>?>([
                new("user", "first", null), new("assistant", "second", null)]),
            _ => throw new InvalidOperationException(method.Name)
        };
        private async Task<SessionInfo?> Get(string owner, string id)
        {
            var record = await Store.GetAsync(owner, id);
            return record is null ? null : new SessionInfo
            {
                Id = id, Title = record.Title, Owner = owner, Mode = record.Mode, Phase = record.Status
            };
        }
    }

    private sealed class NoLive : ILiveTranscriptReader
    {
        public Task<IReadOnlyList<TranscriptEntry>?> ReadAsync(SessionInfo session, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null);
    }

    [PostgreSqlFact]
    public async Task RealHttp_TokenResolution_ProjectIsolation_InboxPaging_AndCatalogProjection()
    {
        await using var db = await PostgresMessageDatabase.CreateAsync();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Postgres"] = db.ConnectionString }).Build();
        var tokens = new ApiTokenStore(cfg);
        var projects = new PostgresProjectStore(cfg);
        await tokens.InitializeAsync();
        await projects.InitializeAsync();
        await tokens.CreateAsync("alice", "full", "oah_full");
        await tokens.CreateAsync("alice", "restricted", "oah_restricted", new ApiTokenScope());
        await tokens.CreateAsync("bob", "other", "oah_other");
        var project = await projects.CreateAsync("alice", new CreateProjectRequest("Owned", null));
        var foreign = await projects.CreateAsync("bob", new CreateProjectRequest("Foreign", null));
        await db.Sessions.UpsertAsync(new SessionRecord
        {
            Id = "owned", Owner = "alice", Title = "Owned", Mode = SessionMode.Interactive,
            AgentSessionId = "native", CallbackToken = "callback", Status = "Succeeded"
        });
        foreach (var id in new[] { "a", "b", "c" })
            await db.Messages.AddAsync(new SessionMessageRecord
            {
                Id = id, Owner = "alice", ToSessionId = "owned", Body = id,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        var catalog = new InMemoryMcpServerStore();
        catalog.Add("alice", "own");
        catalog.Add("bob", "hidden");
        var library = new LibraryAccessService(catalog, new InMemorySkillStore(),
            new FakeLibraryShareReader(), new FakeEnterpriseLicense(false));
        var sessions = DispatchProxy.Create<ISessionService, SessionReads>();
        ((SessionReads)(object)sessions).Store = db.Sessions;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton<IProjectStore>(projects);
        builder.Services.AddSingleton<ISessionMessageStore>(db.Messages);
        builder.Services.AddSingleton<ILibraryAccess>(library);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ISessionAccessService>(new SessionAccessService(
            new NoSessionAccessStore(), new FakeEnterpriseLicense(false)));
        builder.Services.AddSingleton<ILiveTranscriptReader>(new NoLive());
        builder.Services.AddControllers().AddApplicationPart(typeof(RemoteReadController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var path in new[] { "projects", $"projects/{project.Id}", "mcp-servers",
                         "sessions/owned/messages", "sessions/owned/conversation" })
                Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/remote/" + path)).StatusCode);
            foreach (var token in new[] { "oah_full", "oah_restricted" })
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var list = await Json(http, "projects");
                Assert.Equal(project.Id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetString());
                Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/remote/projects/{foreign.Id}")).StatusCode);
                var first = await Json(http, "sessions/owned/messages?limit=2");
                Assert.Equal(["c", "b"], first.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
                var cursor = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
                var last = await Json(http, "sessions/owned/messages?limit=2&cursor=" + cursor);
                Assert.Equal("a", Assert.Single(last.GetProperty("messages").EnumerateArray()).GetProperty("id").GetString());
                Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
                var servers = await Json(http, "mcp-servers");
                Assert.Equal(["id", "name"], Assert.Single(servers.EnumerateArray()).EnumerateObject().Select(p => p.Name));
                var page = await Json(http, "sessions/owned/conversation?offset=1&max=1");
                Assert.Equal("second", Assert.Single(page.GetProperty("entries").EnumerateArray()).GetProperty("text").GetString());
                Assert.Equal(2, page.GetProperty("nextOffset").GetInt32());
                Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/remote/sessions/owned/messages?cursor=bad")).StatusCode);
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "oah_other");
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/remote/sessions/owned/messages")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/remote/sessions/owned/conversation")).StatusCode);
            Assert.All(await db.Messages.ListRecentAsync("owned", 10), m => Assert.Null(m.DeliveredAt));
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<JsonElement> Json(HttpClient http, string path)
    {
        using var response = await http.GetAsync("/api/remote/" + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private sealed class NoSessionAccessStore : ISessionAccessStore
    {
        public Task<StoredSessionAccess?> FindUserAccessAsync(string principal, string sessionId, CancellationToken ct = default)
            => Task.FromResult<StoredSessionAccess?>(null);
        public Task<StoredSessionAccess?> FindTokenAccessAsync(string token, CancellationToken ct = default)
            => throw new InvalidOperationException();
    }
}
