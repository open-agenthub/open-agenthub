using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHub.Api.Library;

/// <summary>
/// Builds the effective .mcp.json for a session from library server entries
/// plus the session's inline config. Inline entries win on name conflicts,
/// so a user can always override a shared definition explicitly.
/// </summary>
public static class McpConfigAssembler
{
    /// <summary>Returns the merged .mcp.json, or null when there is nothing to mount.</summary>
    public static string? Merge(string? inlineJson, IEnumerable<McpServerRecord> servers)
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
            JsonNode? config;
            try
            {
                config = JsonNode.Parse(server.ConfigJson);
            }
            catch (JsonException)
            {
                throw new ArgumentException($"Saved MCP server '{server.Name}' has invalid JSON.");
            }
            inlineServers[server.Name] = config;
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
}
