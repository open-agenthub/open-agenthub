using System.Text.Json;

namespace AgentHub.Api.Library;

public static class LibraryValidation
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 500;
    public const int MaxMcpConfigBytes = 64_000;

    public static string ValidateKind(string? kind)
    {
        var value = kind?.Trim().ToLowerInvariant() ?? "";
        if (value is not ("raw" or "api"))
            throw new ArgumentException("MCP server kind must be 'raw' or 'api'.");
        return value;
    }

    /// <summary>Plain MCP server name: becomes the key in mcpServers, so the same
    /// rules apply as for the sharing policy's server names.</summary>
    public static string ValidateMcpServerName(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is 0 or > MaxNameLength
            || value.StartsWith("mcp__", StringComparison.Ordinal)
            || value.Contains("__", StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("MCP server name must be a plain server name (no whitespace, no '__').");
        }
        return value;
    }

    public static string ValidateDescription(string? description)
    {
        var value = description?.Trim() ?? "";
        if (value.Length > MaxDescriptionLength)
            throw new ArgumentException($"Description must be at most {MaxDescriptionLength} characters.");
        return value;
    }

    /// <summary>
    /// Validates config for the given kind.
    /// <c>raw</c>: single JSON object (one server entry, not a full .mcp.json).
    /// <c>api</c>: object with required non-empty <c>specUrl</c>.
    /// </summary>
    public static string ValidateMcpServerConfig(string? configJson, string kind)
    {
        var normalizedKind = ValidateKind(kind);
        var value = configJson?.Trim() ?? "";
        if (value.Length == 0)
            throw new ArgumentException("MCP server config is required.");
        if (value.Length > MaxMcpConfigBytes)
            throw new ArgumentException("MCP server config is too large.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            throw new ArgumentException("MCP server config is not valid JSON.");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("MCP server config must be a JSON object.");

            if (normalizedKind == "raw")
            {
                if (doc.RootElement.TryGetProperty("mcpServers", out _))
                    throw new ArgumentException(
                        "Provide a single server entry, not a full .mcp.json (no top-level mcpServers).");
            }
            else
            {
                if (!doc.RootElement.TryGetProperty("specUrl", out var specUrl)
                    || specUrl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(specUrl.GetString()))
                {
                    throw new ArgumentException("API MCP config requires a non-empty specUrl.");
                }
            }
        }

        return value;
    }
}
