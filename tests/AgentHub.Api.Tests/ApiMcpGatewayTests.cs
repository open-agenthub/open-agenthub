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

public class ApiMcpGatewayTests
{
    private static string FixturePath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "fixtures", "openapi", "petstore-mini.json"));

    private static (
        ApiMcpGatewayHandler Handler,
        IMcpGatewayTokenService Tokens,
        InMemoryMcpServerStore Store,
        FakeGatewaySessionStore Sessions,
        FakeLibraryShareReader Shares,
        FakeEnterpriseLicense License) CreateFixture(
        HttpMessageHandler? upstream = null,
        bool licenseEnabled = false)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var sp = services.BuildServiceProvider();
        var tokens = new McpGatewayTokenService(sp.GetRequiredService<IDataProtectionProvider>());
        var store = new InMemoryMcpServerStore();
        var sessions = new FakeGatewaySessionStore();
        sessions.Upsert(Session("alice", "sess-1"));
        var shares = new FakeLibraryShareReader();
        var license = new FakeEnterpriseLicense(licenseEnabled);
        var access = new LibraryAccessService(store, shares, license);
        var http = new HttpClient(upstream ?? new StaticSpecHandler(File.ReadAllText(FixturePath)));
        var cache = new OpenApiSpecCache(http);
        var handler = new ApiMcpGatewayHandler(store, tokens, cache, http, sessions, access);
        return (handler, tokens, store, sessions, shares, license);
    }

    private static SessionRecord Session(string owner, string id) => new()
    {
        Id = id,
        Owner = owner,
        CallbackToken = "cb-" + id,
        Mode = SessionMode.Interactive,
        AgentSessionId = "agent-" + id
    };

    private static DefaultHttpContext Ctx(string? token, string body, string method = "POST")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (token is not null)
            ctx.Request.Headers[McpGatewayTokenService.HeaderName] = token;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task Rejects_WithoutToken()
    {
        var (handler, _, store, _, _, _) = CreateFixture();
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""");

        var ctx = Ctx(null, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Rejects_InvalidToken()
    {
        var (handler, _, store, _, _, _) = CreateFixture();
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""");

        var ctx = Ctx("bogus", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Rejects_WhenSessionMissing()
    {
        var (handler, tokens, store, sessions, _, _) = CreateFixture();
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""");
        var token = tokens.Issue("sess-gone", server.Id, "alice");
        // sess-1 exists by default; sess-gone does not

        var ctx = Ctx(token, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        _ = sessions; // keep fixture shape
    }

    [Fact]
    public async Task Rejects_WhenCatalogAccessLost()
    {
        var (handler, tokens, store, sessions, shares, _) = CreateFixture(licenseEnabled: true);
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""");
        sessions.Upsert(Session("bob", "sess-bob"));
        // No share → bob cannot access alice's personal entry
        var token = tokens.Issue("sess-bob", server.Id, "bob");

        var ctx = Ctx(token, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(shares.Ids);
    }

    [Fact]
    public async Task ToolsList_ReturnsPetstoreOperations()
    {
        var (handler, tokens, store, _, _, _) = CreateFixture();
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""");
        var token = tokens.Issue("sess-1", server.Id, "alice");

        var ctx = Ctx(token, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(3, tools.GetArrayLength());
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("listPets", names);
        Assert.Contains("createPet", names);
        Assert.Contains("getPetById", names);
    }

    [Fact]
    public async Task ToolsCall_ProxiesWithBearerSecret()
    {
        var upstream = new RecordingUpstreamHandler();
        var (handler, tokens, store, _, _, _) = CreateFixture(upstream);
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json","baseUrl":"https://petstore.example.test/v1","auth":{"type":"bearer"}}""",
            secretJson: """{"token":"upstream-secret"}""");
        var token = tokens.Issue("sess-1", server.Id, "alice");

        var body = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"getPetById","arguments":{"petId":"abc"}}}""";
        var ctx = Ctx(token, body);
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.NotNull(upstream.LastRequest);
        Assert.Equal(HttpMethod.Get, upstream.LastRequest!.Method);
        Assert.Equal("https://petstore.example.test/v1/pets/abc", upstream.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer upstream-secret", upstream.LastRequest.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task ToolsCall_SharedPersonalApi_DoesNotInjectOwnerBearer()
    {
        var upstream = new RecordingUpstreamHandler();
        var (handler, tokens, store, sessions, shares, _) = CreateFixture(upstream, licenseEnabled: true);
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json","baseUrl":"https://petstore.example.test/v1","auth":{"type":"bearer"}}""",
            secretJson: """{"token":"alice-owner-secret"}""");
        shares.Ids.Add(server.Id);
        sessions.Upsert(Session("bob", "sess-bob"));
        var token = tokens.Issue("sess-bob", server.Id, "bob");

        var body = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"getPetById","arguments":{"petId":"abc"}}}""";
        var ctx = Ctx(token, body);
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("owner-only", result.GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(upstream.LastRequest); // must not proxy with owner's secret
    }

    [Fact]
    public async Task ToolsCall_OrgSharedSecret_InjectsForOtherConsumer()
    {
        var upstream = new RecordingUpstreamHandler();
        var (handler, tokens, store, _, _, _) = CreateFixture(upstream);
        var server = store.Add(McpServerRecord.OrgOwner, "org-pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json","baseUrl":"https://petstore.example.test/v1","auth":{"type":"bearer"}}""",
            secretJson: """{"token":"org-shared-secret"}""");
        var token = tokens.Issue("sess-1", server.Id, "alice");

        var body = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"getPetById","arguments":{"petId":"abc"}}}""";
        var ctx = Ctx(token, body);
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("Bearer org-shared-secret", upstream.LastRequest!.Headers.Authorization?.ToString());
    }

    /// <summary>Returns the petstore fixture for any GET (spec fetch).</summary>
    private sealed class StaticSpecHandler(string specJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(specJson, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Serves the fixture on GET (spec) and records other upstream calls.</summary>
    private sealed class RecordingUpstreamHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        private readonly string _spec = File.ReadAllText(
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "fixtures", "openapi", "petstore-mini.json")));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_spec, Encoding.UTF8, "application/json")
                });
            }

            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"abc","name":"Fido"}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeGatewaySessionStore : ISessionStore
    {
        private readonly Dictionary<string, SessionRecord> _byKey = new(StringComparer.Ordinal);

        public void Upsert(SessionRecord session) =>
            _byKey[$"{session.Owner}\n{session.Id}"] = session;

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
