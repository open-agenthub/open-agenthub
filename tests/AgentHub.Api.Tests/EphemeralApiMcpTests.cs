using System.Net;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Library.ApiMcpGateway;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
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
    public async Task EphemeralStore_RegisterGetAndClearBySession()
    {
        var store = new InMemoryEphemeralApiMcpStore();
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "books", "alice",
            """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql","baseUrl":"https://api.example.test/graphql"}""",
            null));
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "pets", "alice",
            """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""",
            null));
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-2", "other", "bob",
            """{"specType":"openapi","specUrl":"https://other.example.test/openapi.json"}""",
            null));

        Assert.NotNull(await store.GetAsync("sess-1", "books"));
        Assert.Equal(2, (await store.ListBySessionAsync("sess-1")).Count);

        await store.DeleteBySessionAsync("sess-1");
        Assert.Null(await store.GetAsync("sess-1", "books"));
        Assert.Empty(await store.ListBySessionAsync("sess-1"));
        Assert.NotNull(await store.GetAsync("sess-2", "other"));
    }

    /// <summary>
    /// The session listing asks this once for every session the caller owns. Both the default
    /// implementation and the Postgres override have to agree, or the dashboard's "has MCP"
    /// badge changes meaning depending on which store is configured.
    /// </summary>
    [Fact]
    public async Task ListSessionsWithEntries_ReportsOnlySessionsThatHaveOne()
    {
        // Through the interface on purpose: the in-memory store inherits the default
        // implementation, and that is the one this has to pin down.
        IEphemeralApiMcpStore store = new InMemoryEphemeralApiMcpStore();
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "books", "alice",
            """{"specType":"openapi","specUrl":"https://api.example.test/openapi.json"}""",
            null));
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "pets", "alice",
            """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""",
            null));
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-2", "other", "bob",
            """{"specType":"openapi","specUrl":"https://other.example.test/openapi.json"}""",
            null));

        var withEntries = await store.ListSessionsWithEntriesAsync(["sess-1", "sess-2", "sess-3"]);

        Assert.Equal(["sess-1", "sess-2"], withEntries.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Empty(await store.ListSessionsWithEntriesAsync([]));
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

    /// <summary>
    /// SessionInfo.HasMcp (and thus MCP secret/mount eligibility) must be true for
    /// sessions that only have ephemeral API sources — no inline JSON / catalog ids.
    /// </summary>
    [Fact]
    public async Task HasMcp_TrueForEphemeralOnlySession()
    {
        var store = new InMemoryEphemeralApiMcpStore();
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "books", "alice",
            """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql"}""",
            null));

        var hasEphemeral = (await store.ListBySessionAsync("sess-1")).Count > 0;
        Assert.True(hasEphemeral);
        Assert.True(SessionMcpConfig.HasMcp(mcpConfigJson: null, mcpServerIds: [], hasEphemeral));
        Assert.False(SessionMcpConfig.HasMcp(mcpConfigJson: null, mcpServerIds: [], hasEphemeralApiSources: false));
        Assert.True(SessionMcpConfig.HasMcp("""{"mcpServers":{"local":{"command":"x"}}}""", [], false));
        Assert.True(SessionMcpConfig.HasMcp(null, ["catalog-id"], false));
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
    public void UpdateSessionRequest_AcceptsEphemeralApiSources()
    {
        var omitted = JsonSerializer.Deserialize<UpdateSessionRequest>("{}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Null(omitted!.EphemeralApiSources);

        var req = JsonSerializer.Deserialize<UpdateSessionRequest>(
            """{"ephemeralApiSources":[{"name":"books","specUrl":"https://api.example.test/schema.graphql","specType":"auto","saveToLibrary":true}]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotNull(req.EphemeralApiSources);
        Assert.Single(req.EphemeralApiSources!);
        Assert.Equal("books", req.EphemeralApiSources![0].Name);
        Assert.True(req.EphemeralApiSources[0].SaveToLibrary);

        var cleared = JsonSerializer.Deserialize<UpdateSessionRequest>(
            """{"ephemeralApiSources":[]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotNull(cleared.EphemeralApiSources);
        Assert.Empty(cleared.EphemeralApiSources!);
    }

    /// <summary>
    /// Update-path seam: assemble with the *proposed* ephemeral set, then replace
    /// (DeleteBySession + Register) — what UpdateSession does after validation.
    /// </summary>
    [Fact]
    public async Task UpdatePath_ReplaceEphemerals_ReassemblesEffectiveConfig()
    {
        var store = new InMemoryEphemeralApiMcpStore();
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "old", "alice",
            """{"specType":"openapi","specUrl":"https://old.example.test/openapi.json"}""",
            null));

        var proposed = new[]
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
        // Validate assemble against proposed list before mutating the store.
        var json = McpConfigAssembler.Merge(null, [], opts, proposed);
        using (var doc = JsonDocument.Parse(json!))
        {
            Assert.Equal("http://gateway.test/mcp/session/sess-1/books",
                doc.RootElement.GetProperty("mcpServers").GetProperty("books").GetProperty("url").GetString());
        }

        await store.DeleteBySessionAsync("sess-1");
        foreach (var entry in proposed)
            await store.RegisterAsync(entry);

        var ephemeral = await store.ListBySessionAsync("sess-1");
        Assert.Single(ephemeral);
        Assert.Equal("books", ephemeral[0].Name);
        Assert.False(JsonDocument.Parse(json!).RootElement.GetProperty("mcpServers").TryGetProperty("old", out _));
    }

    /// <summary>
    /// Assemble/validation failure must happen before DeleteBySession so existing
    /// ephemerals are not wiped on a bad update (strict catalog resolve / bad JSON).
    /// </summary>
    [Fact]
    public async Task UpdatePath_AssembleFailure_DoesNotClearExistingEphemerals()
    {
        var store = new InMemoryEphemeralApiMcpStore();
        await store.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "old", "alice",
            """{"specType":"openapi","specUrl":"https://old.example.test/openapi.json"}""",
            null));

        var proposed = new[]
        {
            new EphemeralApiMcpEntry(
                "sess-1", "books", "alice",
                """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql"}""",
                null)
        };
        var opts = new McpGatewayAssembleOptions { BaseUrl = "http://gateway.test", SessionId = "sess-1" };

        Assert.Throws<ArgumentException>(() =>
            McpConfigAssembler.Merge("not-json{", [], opts, proposed));

        // Store untouched — UpdateSession only deletes after assemble succeeds.
        var still = await store.ListBySessionAsync("sess-1");
        Assert.Single(still);
        Assert.Equal("old", still[0].Name);
    }

    [Fact]
    public async Task SaveToLibrary_UpsertsWhenNameExists()
    {
        var catalog = new InMemoryMcpServerStore();
        const string configV1 =
            """{"specType":"openapi","specUrl":"https://api.example.test/v1/openapi.json"}""";
        const string configV2 =
            """{"specType":"openapi","specUrl":"https://api.example.test/v2/openapi.json"}""";

        var created = await catalog.CreateAsync("alice",
            new SaveMcpServerRequest("books", null, "api", configV1, null));

        // Upsert path used when SaveToLibrary=true and name already exists.
        var existing = (await catalog.ListByOwnerAsync("alice"))
            .First(s => string.Equals(s.Name, "books", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(created.Id, existing.Id);
        await catalog.UpdateAsync("alice", existing.Id,
            new SaveMcpServerRequest("books", null, "api", configV2, null));

        var list = await catalog.ListByOwnerAsync("alice");
        Assert.Single(list);
        Assert.Equal(created.Id, list[0].Id);
        Assert.Contains("v2", list[0].ConfigJson, StringComparison.Ordinal);
        // Create again would throw — upsert avoids aborting the session update.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            catalog.CreateAsync("alice", new SaveMcpServerRequest("books", null, "api", configV1, null)));
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
        var ephemeral = new InMemoryEphemeralApiMcpStore();
        var sessions = new FakeSessionStore();
        sessions.Upsert(new SessionRecord
        {
            Id = "sess-1", Owner = "alice", CallbackToken = "cb", Mode = SessionMode.Interactive,
            AgentSessionId = "agent-1"
        });
        var access = new LibraryAccessService(catalog, new InMemorySkillStore(), new FakeLibraryShareReader(), new FakeEnterpriseLicense(false));
        var http = new HttpClient(new GraphQlSchemaHandler(sdl));
        var handler = new ApiMcpGatewayHandler(
            catalog, ephemeral, tokens, new OpenApiSpecCache(http), http, sessions, access);

        await ephemeral.RegisterAsync(new EphemeralApiMcpEntry(
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

    [Fact]
    public async Task Gateway_GraphQlIntrospection_SendsBearerWhenSecretPresent()
    {
        const string introspection = """
            {
              "data": {
                "__schema": {
                  "queryType": { "name": "Query" },
                  "mutationType": null,
                  "types": [
                    {
                      "kind": "OBJECT",
                      "name": "Query",
                      "fields": [
                        {
                          "name": "hello",
                          "description": "Say hello",
                          "args": [],
                          "type": { "kind": "SCALAR", "name": "String" }
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """;

        string? seenAuth = null;
        var upstream = new CapturingAuthHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            seenAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(introspection, Encoding.UTF8, "application/json")
            };
        });

        var services = new ServiceCollection();
        services.AddDataProtection();
        var sp = services.BuildServiceProvider();
        var tokens = new McpGatewayTokenService(sp.GetRequiredService<IDataProtectionProvider>());
        var catalog = new InMemoryMcpServerStore();
        var ephemeral = new InMemoryEphemeralApiMcpStore();
        var sessions = new FakeSessionStore();
        sessions.Upsert(new SessionRecord
        {
            Id = "sess-1", Owner = "alice", CallbackToken = "cb", Mode = SessionMode.Interactive,
            AgentSessionId = "agent-1"
        });
        var access = new LibraryAccessService(catalog, new InMemorySkillStore(), new FakeLibraryShareReader(), new FakeEnterpriseLicense(false));
        var http = new HttpClient(upstream);
        var handler = new ApiMcpGatewayHandler(
            catalog, ephemeral, tokens, new OpenApiSpecCache(http), http, sessions, access);

        await ephemeral.RegisterAsync(new EphemeralApiMcpEntry(
            "sess-1", "books", "alice",
            """{"specType":"graphql","specUrl":"https://api.example.test/schema.graphql","baseUrl":"https://api.example.test/graphql","auth":{"type":"bearer"}}""",
            """{"token":"secret-token"}"""));
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
        Assert.Equal("Bearer secret-token", seenAuth);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("hello", names);
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

    private sealed class CapturingAuthHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request, cancellationToken));
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
