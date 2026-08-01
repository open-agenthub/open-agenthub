using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHub.Api.Library;

/// <summary>Options for emitting HTTP MCP gateway URLs for catalog <c>kind=api</c> and ephemeral entries.</summary>
public sealed class McpGatewayAssembleOptions
{
    /// <summary>Base URL agents use to reach the gateway (no trailing slash), e.g. in-cluster service URL.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Session id used when emitting ephemeral <c>/mcp/session/{sessionId}/{name}</c> URLs.</summary>
    public string? SessionId { get; init; }

    /// <summary>Issues a session-bound token for a catalog MCP server id. When null, no auth header is emitted.</summary>
    public Func<string, string>? IssueToken { get; init; }

    /// <summary>Issues a session-bound token for an ephemeral source name. When null, no auth header is emitted.</summary>
    public Func<string, string>? IssueEphemeralToken { get; init; }
}

/// <summary>
/// Builds the effective .mcp.json for a session from catalog server entries
/// plus the session's inline config. Inline entries win on name conflicts,
/// so a user can always override a catalog definition explicitly.
/// </summary>
public static class McpConfigAssembler
{
    /// <summary>Fallback gateway origin used when no <see cref="McpGatewayAssembleOptions"/> are supplied (unit tests / create-path validation).</summary>
    public const string DefaultGatewayBaseUrl = "https://mcp.invalid";

    /// <summary>Returns the merged .mcp.json, or null when there is nothing to mount.</summary>
    public static string? Merge(
        string? inlineJson,
        IEnumerable<McpServerRecord> servers,
        McpGatewayAssembleOptions? gateway = null,
        IEnumerable<EphemeralApiMcpEntry>? ephemeral = null)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(inlineJson))
        {
            root = new JsonObject();
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(inlineJson);
            }
            catch (JsonException)
            {
                throw new ArgumentException("MCP config is not valid JSON.");
            }
            root = parsed as JsonObject
                ?? throw new ArgumentException("MCP config must be a JSON object.");
        }

        if (root["mcpServers"] is not JsonObject inlineServers)
        {
            if (root.ContainsKey("mcpServers"))
                throw new ArgumentException("mcpServers must be a JSON object.");
            inlineServers = new JsonObject();
            root["mcpServers"] = inlineServers;
        }

        foreach (var server in servers)
        {
            if (inlineServers.ContainsKey(server.Name))
                continue; // inline definition wins
            inlineServers[server.Name] = BuildServerEntry(server, gateway);
        }

        if (ephemeral is not null)
        {
            foreach (var entry in ephemeral)
            {
                if (inlineServers.ContainsKey(entry.Name))
                    continue; // inline definition wins
                inlineServers[entry.Name] = BuildEphemeralEntry(entry, gateway);
            }
        }

        if (inlineServers.Count == 0)
        {
            // A config that only carried an empty mcpServers object means "nothing to mount"
            // unless the inline JSON had other top-level content worth preserving.
            root.Remove("mcpServers");
            if (root.Count == 0)
                return null;
            root["mcpServers"] = new JsonObject();
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonNode BuildServerEntry(McpServerRecord server, McpGatewayAssembleOptions? gateway)
    {
        if (string.Equals(server.Kind, "api", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = string.IsNullOrWhiteSpace(gateway?.BaseUrl)
                ? DefaultGatewayBaseUrl
                : gateway!.BaseUrl.TrimEnd('/');
            var entry = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"{baseUrl}/mcp/api/{server.Id}"
            };

            if (gateway?.IssueToken is not null)
            {
                entry["headers"] = new JsonObject
                {
                    [McpGatewayTokenService.HeaderName] = gateway.IssueToken(server.Id)
                };
            }

            return entry;
        }

        // kind=raw (default): ConfigJson is the server entry object.
        JsonNode? config;
        try
        {
            config = JsonNode.Parse(server.ConfigJson);
        }
        catch (JsonException)
        {
            throw new ArgumentException($"Saved MCP server '{server.Name}' has invalid JSON.");
        }

        return config as JsonObject
            ?? throw new ArgumentException($"Saved MCP server '{server.Name}' config must be a JSON object.");
    }

    private static JsonNode BuildEphemeralEntry(EphemeralApiMcpEntry ephemeral, McpGatewayAssembleOptions? gateway)
    {
        var baseUrl = string.IsNullOrWhiteSpace(gateway?.BaseUrl)
            ? DefaultGatewayBaseUrl
            : gateway!.BaseUrl.TrimEnd('/');
        var sessionId = !string.IsNullOrWhiteSpace(gateway?.SessionId)
            ? gateway!.SessionId!.Trim()
            : ephemeral.SessionId.Trim();
        var entry = new JsonObject
        {
            ["type"] = "http",
            ["url"] = $"{baseUrl}/mcp/session/{sessionId}/{ephemeral.Name}"
        };

        if (gateway?.IssueEphemeralToken is not null)
        {
            entry["headers"] = new JsonObject
            {
                [McpGatewayTokenService.HeaderName] = gateway.IssueEphemeralToken(ephemeral.Name)
            };
        }

        return entry;
    }
}
