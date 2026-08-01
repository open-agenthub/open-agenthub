using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;

namespace AgentHub.Api.Library.ApiMcpGateway;

/// <summary>
/// Minimal Streamable-HTTP MCP endpoint for catalog <c>kind=api</c> and session-ephemeral
/// OpenAPI/GraphQL entries: JSON-RPC over POST for initialize / tools/list / tools/call.
/// </summary>
public sealed class ApiMcpGatewayHandler
{
    private readonly IMcpServerStore _store;
    private readonly IEphemeralApiMcpStore _ephemeral;
    private readonly IMcpGatewayTokenService _tokens;
    private readonly OpenApiSpecCache _specs;
    private readonly HttpClient _upstream;
    private readonly ISessionStore _sessions;
    private readonly ILibraryAccess _access;

    public ApiMcpGatewayHandler(
        IMcpServerStore store,
        IEphemeralApiMcpStore ephemeral,
        IMcpGatewayTokenService tokens,
        OpenApiSpecCache specs,
        HttpClient upstream,
        ISessionStore sessions,
        ILibraryAccess access)
    {
        _store = store;
        _ephemeral = ephemeral;
        _tokens = tokens;
        _specs = specs;
        _upstream = upstream;
        _sessions = sessions;
        _access = access;
    }

    public async Task HandleCatalogAsync(HttpContext ctx, string id)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var token = ExtractToken(ctx.Request);
        if (!_tokens.TryValidate(token, id, out var claims))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Unauthorized", ctx.RequestAborted);
            return;
        }

        var session = await _sessions.GetAsync(claims.Owner, claims.SessionId, ctx.RequestAborted);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Session not found", ctx.RequestAborted);
            return;
        }

        var servers = await _store.GetManyAsync([id], ctx.RequestAborted);
        var server = servers.FirstOrDefault();
        if (server is null || !string.Equals(server.Kind, "api", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("MCP server not found", ctx.RequestAborted);
            return;
        }

        var accessible = await _access.ResolveMcpServersAsync(
            claims.Owner, [id], strict: false, ctx.RequestAborted);
        if (accessible.Count == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("MCP server not accessible", ctx.RequestAborted);
            return;
        }

        server = accessible[0];
        var canInjectSecret = CanInjectUpstreamSecret(server, claims);
        await HandleRpcAsync(ctx, server.ConfigJson, server.SecretJson, canInjectSecret, claims);
    }

    public async Task HandleSessionAsync(HttpContext ctx, string sessionId, string name)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var token = ExtractToken(ctx.Request);
        if (!_tokens.TryValidateEphemeral(token, sessionId, name, out var claims))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Unauthorized", ctx.RequestAborted);
            return;
        }

        var session = await _sessions.GetAsync(claims.Owner, claims.SessionId, ctx.RequestAborted);
        if (session is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("Session not found", ctx.RequestAborted);
            return;
        }

        var entry = await _ephemeral.GetAsync(sessionId, name, ctx.RequestAborted);
        if (entry is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("Ephemeral MCP source not found", ctx.RequestAborted);
            return;
        }

        if (!string.Equals(entry.Owner, claims.Owner, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Ephemeral MCP source not accessible", ctx.RequestAborted);
            return;
        }

        // Ephemeral secrets are owned by the session owner (same as personal catalog).
        var canInjectSecret = string.Equals(claims.Owner, entry.Owner, StringComparison.Ordinal);
        await HandleRpcAsync(ctx, entry.ConfigJson, entry.SecretJson, canInjectSecret, claims);
    }

    private async Task HandleRpcAsync(
        HttpContext ctx,
        string configJson,
        string? secretJson,
        bool canInjectSecret,
        McpGatewayTokenClaims claims)
    {
        JsonDocument? rpcDoc = null;
        try
        {
            rpcDoc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Invalid JSON-RPC body", ctx.RequestAborted);
            return;
        }

        using (rpcDoc)
        {
            var root = rpcDoc.RootElement;
            var idNode = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default;
            var method = root.TryGetProperty("method", out var methodEl)
                         && methodEl.ValueKind == JsonValueKind.String
                ? methodEl.GetString()!
                : "";

            object? result;
            object? error = null;
            try
            {
                result = method switch
                {
                    "initialize" => BuildInitializeResult(),
                    "notifications/initialized" => null,
                    "ping" => new { },
                    "tools/list" => await ToolsListAsync(
                        configJson, secretJson, canInjectSecret, ctx.RequestAborted),
                    "tools/call" => await ToolsCallAsync(
                        configJson, secretJson, canInjectSecret, claims, root, ctx.RequestAborted),
                    _ => throw new GatewayRpcException(-32601, $"Method not found: {method}")
                };
            }
            catch (GatewayRpcException ex)
            {
                result = null;
                error = new { code = ex.Code, message = ex.Message };
            }
            catch (Exception ex)
            {
                result = null;
                error = new { code = -32000, message = ex.Message };
            }

            // Notifications have no response.
            if (method.StartsWith("notifications/", StringComparison.Ordinal) && error is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            var response = new JsonObject { ["jsonrpc"] = "2.0" };
            if (idNode.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                response["id"] = JsonNode.Parse(idNode.GetRawText());
            if (error is not null)
                response["error"] = JsonSerializer.SerializeToNode(error);
            else
                response["result"] = JsonSerializer.SerializeToNode(result);
            await ctx.Response.WriteAsync(response.ToJsonString(), ctx.RequestAborted);
        }
    }

    private static string? ExtractToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(McpGatewayTokenService.HeaderName, out var custom)
            && !string.IsNullOrWhiteSpace(custom))
            return custom.ToString();

        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        return null;
    }

    private static object BuildInitializeResult() => new
    {
        protocolVersion = "2024-11-05",
        capabilities = new { tools = new { } },
        serverInfo = new { name = "agenthub-api-mcp-gateway", version = "1.0.0" }
    };

    private async Task<object> ToolsListAsync(
        string configJson, string? secretJson, bool canInjectSecret, CancellationToken ct)
    {
        var api = ParseApiConfig(configJson);
        EnsureSafeOutboundUrl(api.SpecUrl, "specUrl");
        if (!string.IsNullOrWhiteSpace(api.BaseUrl))
            EnsureSafeOutboundUrl(api.BaseUrl!, "baseUrl");

        if (IsGraphQl(api))
        {
            var tools = await LoadGraphQlToolsAsync(api, secretJson, canInjectSecret, ct);
            return new
            {
                tools = tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = JsonSerializer.Deserialize<JsonElement>(t.InputSchema.GetRawText())
                }).ToArray()
            };
        }

        var spec = await _specs.GetAsync(api.SpecUrl, ct);
        var openApiTools = OpenApiToolMapper.MapTools(spec);
        return new
        {
            tools = openApiTools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = JsonSerializer.Deserialize<JsonElement>(t.InputSchema.GetRawText())
            }).ToArray()
        };
    }

    private async Task<object> ToolsCallAsync(
        string configJson,
        string? secretJson,
        bool canInjectSecret,
        McpGatewayTokenClaims claims,
        JsonElement rpcRoot,
        CancellationToken ct)
    {
        if (!rpcRoot.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            throw new GatewayRpcException(-32602, "tools/call requires params");

        var toolName = p.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()!
            : throw new GatewayRpcException(-32602, "tools/call requires params.name");

        JsonElement args = default;
        if (p.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
            args = argsEl;

        var api = ParseApiConfig(configJson);
        EnsureSafeOutboundUrl(api.SpecUrl, "specUrl");
        if (!string.IsNullOrWhiteSpace(api.BaseUrl))
            EnsureSafeOutboundUrl(api.BaseUrl!, "baseUrl");

        if (!canInjectSecret && api.Auth.Type is not ("none" or ""))
        {
            return ToolError(
                "This API requires credentials. The catalog entry's secret is owner-only; " +
                "attach your own credentials (not supported in v1) or use an unauthenticated API.");
        }

        if (IsGraphQl(api))
            return await CallGraphQlAsync(api, secretJson, canInjectSecret, toolName, args, ct);

        return await CallOpenApiAsync(api, secretJson, canInjectSecret, toolName, args, ct);
    }

    private async Task<object> CallOpenApiAsync(
        ApiConfig api, string? secretJson, bool canInjectSecret,
        string toolName, JsonElement args, CancellationToken ct)
    {
        var spec = await _specs.GetAsync(api.SpecUrl, ct);
        var tools = OpenApiToolMapper.MapTools(spec);
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal))
            ?? throw new GatewayRpcException(-32602, $"Unknown tool: {toolName}");

        var baseUrl = !string.IsNullOrWhiteSpace(api.BaseUrl)
            ? api.BaseUrl!.TrimEnd('/')
            : OpenApiToolMapper.ReadDefaultBaseUrl(spec)
              ?? throw new GatewayRpcException(-32000, "OpenAPI has no servers.url and config has no baseUrl");
        EnsureSafeOutboundUrl(baseUrl, "baseUrl");

        var path = tool.PathTemplate;
        var query = new List<string>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bodyFields = new JsonObject();
        var boundNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in tool.Parameters)
        {
            boundNames.Add(binding.Name);
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(binding.Name, out var val))
            {
                if (binding.Required)
                    throw new GatewayRpcException(-32602, $"Missing required argument: {binding.Name}");
                continue;
            }

            var text = val.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? val.ToString()
                : val.GetRawText();

            switch (binding.In)
            {
                case "path":
                    path = path.Replace($"{{{binding.Name}}}", Uri.EscapeDataString(text), StringComparison.Ordinal);
                    break;
                case "query":
                    query.Add($"{Uri.EscapeDataString(binding.Name)}={Uri.EscapeDataString(text)}");
                    break;
                case "header":
                    headers[binding.Name] = text;
                    break;
                case "body":
                    bodyFields["body"] = JsonNode.Parse(val.GetRawText());
                    break;
            }
        }

        if (tool.HasJsonBody && args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                if (boundNames.Contains(prop.Name))
                    continue;
                bodyFields[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }

        var url = baseUrl + path;
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        using var request = new HttpRequestMessage(new HttpMethod(tool.Method), url);
        foreach (var (h, v) in headers)
            request.Headers.TryAddWithoutValidation(h, v);

        if (canInjectSecret)
            ApplyUpstreamAuth(request, api.Auth, secretJson);

        if (tool.HasJsonBody && bodyFields.Count > 0)
        {
            string payload;
            if (bodyFields.Count == 1 && bodyFields.ContainsKey("body"))
                payload = bodyFields["body"]!.ToJsonString();
            else
                payload = bodyFields.ToJsonString();
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        using var resp = await _upstream.SendAsync(request, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        var isError = !resp.IsSuccessStatusCode;
        var contentText = isError
            ? $"Upstream HTTP {(int)resp.StatusCode}: {respText}"
            : respText;

        return new
        {
            content = new[] { new { type = "text", text = contentText } },
            isError
        };
    }

    private async Task<object> CallGraphQlAsync(
        ApiConfig api, string? secretJson, bool canInjectSecret,
        string toolName, JsonElement args, CancellationToken ct)
    {
        var tools = await LoadGraphQlToolsAsync(api, secretJson, canInjectSecret, ct);
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal))
            ?? throw new GatewayRpcException(-32602, $"Unknown tool: {toolName}");

        var endpoint = !string.IsNullOrWhiteSpace(api.BaseUrl)
            ? api.BaseUrl!.TrimEnd('/')
            : api.SpecUrl.TrimEnd('/');
        EnsureSafeOutboundUrl(endpoint, "baseUrl");

        foreach (var binding in tool.Arguments)
        {
            if (binding.Required
                && (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(binding.Name, out _)))
                throw new GatewayRpcException(-32602, $"Missing required argument: {binding.Name}");
        }

        var document = BuildGraphQlDocument(tool, args);
        var variables = new JsonObject();
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                if (prop.Name == "_selection")
                    continue;
                variables[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }

        var payload = new JsonObject
        {
            ["query"] = document,
            ["variables"] = variables
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        if (canInjectSecret)
            ApplyUpstreamAuth(request, api.Auth, secretJson);

        using var resp = await _upstream.SendAsync(request, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        var isError = !resp.IsSuccessStatusCode;
        if (!isError)
        {
            try
            {
                using var doc = JsonDocument.Parse(respText);
                if (doc.RootElement.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array
                    && errors.GetArrayLength() > 0)
                    isError = true;
            }
            catch (JsonException)
            {
                // treat as plain text success
            }
        }

        var contentText = isError && !resp.IsSuccessStatusCode
            ? $"Upstream HTTP {(int)resp.StatusCode}: {respText}"
            : respText;

        return new
        {
            content = new[] { new { type = "text", text = contentText } },
            isError
        };
    }

    private async Task<IReadOnlyList<GraphQlMappedTool>> LoadGraphQlToolsAsync(
        ApiConfig api, string? secretJson, bool canInjectSecret, CancellationToken ct)
    {
        string? document = null;
        try
        {
            document = await _specs.GetAsync(api.SpecUrl, ct);
        }
        catch
        {
            // Fall through to introspection against the GraphQL endpoint.
        }

        if (!string.IsNullOrWhiteSpace(document))
        {
            var tools = GraphQlToolMapper.MapTools(document);
            if (tools.Count > 0)
                return tools;
        }

        var endpoint = !string.IsNullOrWhiteSpace(api.BaseUrl) ? api.BaseUrl! : api.SpecUrl;
        EnsureSafeOutboundUrl(endpoint, "baseUrl");
        var introspected = await IntrospectAsync(
            endpoint, api.Auth, secretJson, canInjectSecret, ct);
        return GraphQlToolMapper.MapToolsFromIntrospection(introspected);
    }

    private async Task<string> IntrospectAsync(
        string endpoint,
        ApiAuthConfig auth,
        string? secretJson,
        bool canInjectSecret,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var body = JsonSerializer.Serialize(new { query = GraphQlToolMapper.IntrospectionQuery });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (canInjectSecret)
            ApplyUpstreamAuth(request, auth, secretJson);
        using var resp = await _upstream.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string BuildGraphQlDocument(GraphQlMappedTool tool, JsonElement args)
    {
        var op = tool.Kind == GraphQlOperationKind.Mutation ? "mutation" : "query";
        var varDefs = new List<string>();
        var argPasses = new List<string>();
        foreach (var a in tool.Arguments)
        {
            var typeExpr = a.Required ? $"{a.TypeName}!" : a.TypeName;
            varDefs.Add($"${a.Name}: {typeExpr}");
            argPasses.Add($"{a.Name}: ${a.Name}");
        }

        string selection;
        if (tool.ReturnIsScalar)
        {
            selection = "";
        }
        else if (args.ValueKind == JsonValueKind.Object
                 && args.TryGetProperty("_selection", out var sel)
                 && sel.ValueKind == JsonValueKind.String
                 && !string.IsNullOrWhiteSpace(sel.GetString()))
        {
            selection = " " + sel.GetString()!.Trim();
        }
        else
        {
            selection = " { __typename }";
        }

        var varClause = varDefs.Count > 0 ? $"({string.Join(", ", varDefs)})" : "";
        var argClause = argPasses.Count > 0 ? $"({string.Join(", ", argPasses)})" : "";
        return $"{op} {tool.Name}{varClause} {{ {tool.FieldName}{argClause}{selection} }}";
    }

    private static bool IsGraphQl(ApiConfig api)
    {
        if (api.SpecType is "graphql")
            return true;
        if (api.SpecType is "openapi")
            return false;
        // auto: sniff URL path
        return api.SpecUrl.Contains("graphql", StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(api.BaseUrl)
                   && api.BaseUrl!.Contains("graphql", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Org shared secrets may be injected for any allowed consumer.
    /// Personal secrets are owner-only (claims.Owner must match server.Owner).
    /// </summary>
    internal static bool CanInjectUpstreamSecret(McpServerRecord server, McpGatewayTokenClaims claims)
        => server.Owner == McpServerRecord.OrgOwner
           || string.Equals(claims.Owner, server.Owner, StringComparison.Ordinal);

    private static object ToolError(string message) => new
    {
        content = new[] { new { type = "text", text = message } },
        isError = true
    };

    private static void ApplyUpstreamAuth(HttpRequestMessage request, ApiAuthConfig auth, string? secretJson)
    {
        if (auth.Type is "none" or "")
            return;

        string? token = null;
        string? headerValue = null;
        if (!string.IsNullOrWhiteSpace(secretJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(secretJson);
                if (doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                    token = t.GetString();
                if (doc.RootElement.TryGetProperty("headerValue", out var hv) && hv.ValueKind == JsonValueKind.String)
                    headerValue = hv.GetString();
            }
            catch (JsonException)
            {
                // ignore malformed secret
            }
        }

        if (auth.Type == "bearer" && !string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return;
        }

        if (auth.Type == "header" && !string.IsNullOrEmpty(headerValue))
        {
            var headerName = string.IsNullOrWhiteSpace(auth.HeaderName) ? "Authorization" : auth.HeaderName!;
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }
    }

    private static ApiConfig ParseApiConfig(string configJson)
    {
        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("specUrl", out var specUrlEl)
            || specUrlEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(specUrlEl.GetString()))
            throw new GatewayRpcException(-32000, "API config missing specUrl");

        string? baseUrl = null;
        if (root.TryGetProperty("baseUrl", out var baseEl)
            && baseEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(baseEl.GetString()))
            baseUrl = baseEl.GetString();

        var specType = "auto";
        if (root.TryGetProperty("specType", out var st)
            && st.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(st.GetString()))
            specType = st.GetString()!.Trim().ToLowerInvariant();

        var auth = new ApiAuthConfig("none", null);
        if (root.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
        {
            var type = authEl.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()!.Trim().ToLowerInvariant()
                : "none";
            string? headerName = null;
            if (authEl.TryGetProperty("headerName", out var hn) && hn.ValueKind == JsonValueKind.String)
                headerName = hn.GetString();
            auth = new ApiAuthConfig(type, headerName);
        }

        return new ApiConfig(specUrlEl.GetString()!, baseUrl, specType, auth);
    }

    /// <summary>
    /// Blocks obvious SSRF targets (loopback, link-local, cloud metadata IP).
    /// Hostname-only checks — no DNS resolution in v1. Throws <see cref="ArgumentException"/>.
    /// </summary>
    public static void ValidateSafeOutboundUrl(string url, string fieldName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"{fieldName} must be an absolute http(s) URL");

        var host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{fieldName} targets a blocked host");

        if (!IPAddress.TryParse(host, out var ip))
            return;

        if (IPAddress.IsLoopback(ip)
            || IsLinkLocal(ip)
            || ip.Equals(IPAddress.Parse("169.254.169.254")))
            throw new ArgumentException($"{fieldName} targets a blocked address");
    }

    internal static void EnsureSafeOutboundUrl(string url, string fieldName)
    {
        try
        {
            ValidateSafeOutboundUrl(url, fieldName);
        }
        catch (ArgumentException ex)
        {
            throw new GatewayRpcException(-32000, ex.Message);
        }
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 169 && b[1] == 254;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal;

        return false;
    }

    private sealed record ApiConfig(string SpecUrl, string? BaseUrl, string SpecType, ApiAuthConfig Auth);
    private sealed record ApiAuthConfig(string Type, string? HeaderName);

    private sealed class GatewayRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
