using System.Net;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Library.ApiMcpGateway;
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

    private static (ApiMcpGatewayHandler Handler, IMcpGatewayTokenService Tokens, InMemoryMcpServerStore Store) CreateFixture(
        HttpMessageHandler? upstream = null)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var sp = services.BuildServiceProvider();
        var tokens = new McpGatewayTokenService(sp.GetRequiredService<IDataProtectionProvider>());
        var store = new InMemoryMcpServerStore();
        var http = new HttpClient(upstream ?? new StaticSpecHandler(File.ReadAllText(FixturePath)));
        var cache = new OpenApiSpecCache(http);
        var handler = new ApiMcpGatewayHandler(store, tokens, cache, http);
        return (handler, tokens, store);
    }

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
        var (handler, _, store) = CreateFixture();
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""");

        var ctx = Ctx(null, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Rejects_InvalidToken()
    {
        var (handler, _, store) = CreateFixture();
        var server = store.Add("alice", "pets", kind: "api",
            configJson: """{"specType":"openapi","specUrl":"https://petstore.example.test/openapi.json"}""");

        var ctx = Ctx("bogus", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        await handler.HandleCatalogAsync(ctx, server.Id);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ToolsList_ReturnsPetstoreOperations()
    {
        var (handler, tokens, store) = CreateFixture();
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
        var (handler, tokens, store) = CreateFixture(upstream);
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
}
