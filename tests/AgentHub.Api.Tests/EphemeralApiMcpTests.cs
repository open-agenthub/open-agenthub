using System.Net;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Library.ApiMcpGateway;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHub.Api.Tests;

public class EphemeralApiMcpTests
{
    private static IMcpGatewayTokenService CreateTokens()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        return new McpGatewayTokenService(services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void EphemeralStore_RegisterGetAndClearBySession()
    {
        var store = new EphemeralApiMcpStore();
        store.Register(new EphemeralApiMcpEntry(
            "sess-1", "books", "alice",
            """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql","baseUrl":"https://api.example.test/graphql"}""",
            null));
        store.Register(new EphemeralApiMcpEntry(
            "sess-1", "pets", "alice",
            """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""",
            null));
        store.Register(new EphemeralApiMcpEntry(
            "sess-2", "other", "bob",
            """{"specType":"openapi","specUrl":"https://other.example.test/openapi.json"}""",
            null));

        Assert.NotNull(store.Get("sess-1", "books"));
        Assert.Equal(2, store.ListBySession("sess-1").Count);

        store.DeleteBySession("sess-1");
        Assert.Null(store.Get("sess-1", "books"));
        Assert.Empty(store.ListBySession("sess-1"));
        Assert.NotNull(store.Get("sess-2", "other"));
    }

    [Fact]
    public void EphemeralToken_BindsSessionIdAndName()
    {
        var tokens = CreateTokens();
        var token = tokens.IssueEphemeral("sess-1", "books", "alice");

        Assert.True(tokens.TryValidateEphemeral(token, "sess-1", "books", out var claims));
        Assert.Equal("sess-1", claims.SessionId);
        Assert.Equal("alice", claims.Owner);
        Assert.False(tokens.TryValidateEphemeral(token, "sess-1", "other", out _));
        Assert.False(tokens.TryValidateEphemeral(token, "sess-2", "books", out _));
        Assert.False(tokens.TryValidate(token, "books", out _)); // catalog binding must not accept ephemeral token
    }

    [Fact]
    public void Assembler_EmitsEphemeralHttpEntries()
    {
        var ephemeral = new[]
        {
            new EphemeralApiMcpEntry(
                "sess-1", "books", "alice",
                """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql"}""",
                null)
        };
        var opts = new McpGatewayAssembleOptions
        {
            BaseUrl = "http://gateway.test",
            SessionId = "sess-1",
            IssueToken = id => $"tok-{id}",
            IssueEphemeralToken = name => $"eph-{name}"
        };

        var json = McpConfigAssembler.Merge(null, [], opts, ephemeral);
        using var doc = JsonDocument.Parse(json!);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("books");
        Assert.Equal("http", entry.GetProperty("type").GetString());
        Assert.Equal("http://gateway.test/mcp/session/sess-1/books", entry.GetProperty("url").GetString());
        Assert.Equal("eph-books", entry.GetProperty("headers").GetProperty(McpGatewayTokenService.HeaderName).GetString());
    }

    [Fact]
    public void Assembler_InlineWinsOverEphemeralName()
    {
        var ephemeral = new[]
        {
            new EphemeralApiMcpEntry(
                "sess-1", "books", "alice",
                """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql"}""",
                null)
        };
        var inline = """{"mcpServers":{"books":{"command":"local"}}}""";
        var opts = new McpGatewayAssembleOptions { BaseUrl = "http://gateway.test", SessionId = "sess-1" };
        var json = McpConfigAssembler.Merge(inline, [], opts, ephemeral);
        using var doc = JsonDocument.Parse(json!);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("books");
        Assert.Equal("local", entry.GetProperty("command").GetString());
    }

    [Fact]
    public void CreateSessionRequest_AcceptsEphemeralApiSources()
    {
        var req = new CreateSessionRequest
        {
            Title = "t",
            EphemeralApiSources =
            [
                new EphemeralApiSource
                {
                    Name = "books",
                    SpecUrl = "https://api.example.test/schema.graphql",
                    SpecType = "graphql",
                    BaseUrl = "https://api.example.test/graphql",
                    SaveToLibrary = true
                }
            ]
        };

        Assert.Single(req.EphemeralApiSources);
        Assert.Equal("books", req.EphemeralApiSources[0].Name);
        Assert.True(req.EphemeralApiSources[0].SaveToLibrary);
    }

    [Fact]
    public async Task Gateway_EphemeralGraphQl_ToolsListFromSchemaUrl()
    {
        var sdl = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "graphql", "books.graphql")));
        var services = new ServiceCollection();
        services.AddDataProtection();
        var sp = services.BuildServiceProvider();
        var tokens = new McpGatewayTokenService(sp.GetRequiredService<IDataProtectionProvider>());
        var catalog = new InMemoryMcpServerStore();
        var ephemeral = new EphemeralApiMcpStore();
        var sessions = new FakeSessionStore();
        sessions.Upsert(new SessionRecord
        {
            Id = "sess-1", Owner = "alice", CallbackToken = "cb", Mode = SessionMode.Interactive,
            AgentSessionId = "agent-1"
        });
        var access = new LibraryAccessService(catalog, new FakeLibraryShareReader(), new FakeEnterpriseLicense(false));
        var http = new HttpClient(new GraphQlSchemaHandler(sdl));
        var handler = new ApiMcpGatewayHandler(
            catalog, ephemeral, tokens, new OpenApiSpecCache(http), http, sessions, access);

        ephemeral.Register(new EphemeralApiMcpEntry(
            "sess-1", "books", "alice",
            """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql","baseUrl":"https://api.example.test/graphql"}""",
            null));
        var token = tokens.IssueEphemeral("sess-1", "books", "alice");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        ctx.Request.Headers[McpGatewayTokenService.HeaderName] = token;
        ctx.Response.Body = new MemoryStream();

        await handler.HandleSessionAsync(ctx, "sess-1", "books");

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("book", names);
        Assert.Contains("books", names);
        Assert.Contains("addBook", names);
    }

    private sealed class GraphQlSchemaHandler(string sdl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sdl, Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly Dictionary<string, SessionRecord> _byKey = new(StringComparer.Ordinal);
        public void Upsert(SessionRecord session) => _byKey[$"{session.Owner}\n{session.Id}"] = session;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default)
        {
            Upsert(r);
            return Task.CompletedTask;
        }
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default)
            => Task.FromResult(_byKey.TryGetValue($"{owner}\n{id}", out var s) ? s : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_byKey.Values.FirstOrDefault(s => s.Id == id));
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default)
            => Task.FromResult(_byKey.Values.FirstOrDefault(s => s.CallbackToken == token));
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionRecord>>(
                _byKey.Values.Where(s => s.Owner == owner).ToList());
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            foreach (var key in _byKey.Keys.Where(k => k.EndsWith("\n" + id, StringComparison.Ordinal)).ToList())
                _byKey.Remove(key);
            return Task.CompletedTask;
        }
    }
}
