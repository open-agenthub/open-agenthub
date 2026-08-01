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
/// Minimal Streamable-HTTP MCP endpoint for catalog <c>kind=api</c> OpenAPI entries:
/// JSON-RPC over POST for initialize / tools/list / tools/call.
/// </summary>
public sealed class ApiMcpGatewayHandler
{
    private readonly IMcpServerStore _store;
    private readonly IMcpGatewayTokenService _tokens;
    private readonly OpenApiSpecCache _specs;
    private readonly HttpClient _upstream;
    private readonly ISessionStore _sessions;
    private readonly ILibraryAccess _access;

    public ApiMcpGatewayHandler(
        IMcpServerStore store,
        IMcpGatewayTokenService tokens,
        OpenApiSpecCache specs,
        HttpClient upstream,
        ISessionStore sessions,
        ILibraryAccess access)
    {
        _store = store;
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
                    "tools/list" => await ToolsListAsync(server, ctx.RequestAborted),
                    "tools/call" => await ToolsCallAsync(server, claims, root, ctx.RequestAborted),
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

    private async Task<object> ToolsListAsync(McpServerRecord server, CancellationToken ct)
    {
        var (specUrl, _, _) = ParseApiConfig(server.ConfigJson);
        EnsureSafeOutboundUrl(specUrl, "specUrl");
        var spec = await _specs.GetAsync(specUrl, ct);
        var tools = OpenApiToolMapper.MapTools(spec);
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

    private async Task<object> ToolsCallAsync(
        McpServerRecord server, McpGatewayTokenClaims claims, JsonElement rpcRoot, CancellationToken ct)
    {
        if (!rpcRoot.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            throw new GatewayRpcException(-32602, "tools/call requires params");

        var toolName = p.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()!
            : throw new GatewayRpcException(-32602, "tools/call requires params.name");

        JsonElement args = default;
        if (p.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
            args = argsEl;

        var (specUrl, baseUrlOverride, auth) = ParseApiConfig(server.ConfigJson);
        EnsureSafeOutboundUrl(specUrl, "specUrl");
        var spec = await _specs.GetAsync(specUrl, ct);
        var tools = OpenApiToolMapper.MapTools(spec);
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal))
            ?? throw new GatewayRpcException(-32602, $"Unknown tool: {toolName}");

        var baseUrl = !string.IsNullOrWhiteSpace(baseUrlOverride)
            ? baseUrlOverride!.TrimEnd('/')
            : OpenApiToolMapper.ReadDefaultBaseUrl(spec)
              ?? throw new GatewayRpcException(-32000, "OpenAPI has no servers.url and config has no baseUrl");
        EnsureSafeOutboundUrl(baseUrl, "baseUrl");

        var canInjectSecret = CanInjectUpstreamSecret(server, claims);
        if (!canInjectSecret && auth.Type is not ("none" or ""))
        {
            return ToolError(
                "This API requires credentials. The catalog entry's secret is owner-only; " +
                "attach your own credentials (not supported in v1) or use an unauthenticated API.");
        }

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
            ApplyUpstreamAuth(request, auth, server.SecretJson);

        if (tool.HasJsonBody && bodyFields.Count > 0)
        {
            // Flattened body fields (normal object) vs single "body" wrapper.
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

    private static (string SpecUrl, string? BaseUrl, ApiAuthConfig Auth) ParseApiConfig(string configJson)
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

        return (specUrlEl.GetString()!, baseUrl, auth);
    }

    /// <summary>
    /// Blocks obvious SSRF targets (loopback, link-local, cloud metadata IP).
    /// Hostname-only checks — no DNS resolution in v1.
    /// </summary>
    internal static void EnsureSafeOutboundUrl(string url, string fieldName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new GatewayRpcException(-32000, $"{fieldName} must be an absolute http(s) URL");

        var host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new GatewayRpcException(-32000, $"{fieldName} targets a blocked host");

        if (!IPAddress.TryParse(host, out var ip))
            return;

        if (IPAddress.IsLoopback(ip)
            || IsLinkLocal(ip)
            || ip.Equals(IPAddress.Parse("169.254.169.254")))
            throw new GatewayRpcException(-32000, $"{fieldName} targets a blocked address");
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

    private sealed record ApiAuthConfig(string Type, string? HeaderName);

    private sealed class GatewayRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
