using System.Text.Json;

namespace AgentHub.Api.Library;

/// <summary>
/// A reusable MCP server definition saved in a user's account library.
/// ConfigJson is a single server entry (the value side of an ".mcp.json"
/// mcpServers object), keyed by Name when applied to a session.
/// </summary>
public sealed class McpServerRecord
{
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A reusable agent skill (SKILL.md) saved in a user's account library.
/// The markdown content lives in S3 when available; otherwise it is kept
/// in the fallback content column (see SkillStore).
/// </summary>
public sealed class SkillRecord
{
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>True when the content lives in S3; false = fallback content column.</summary>
    public bool ContentInS3 { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record SaveMcpServerRequest(string Name, string? Description, string ConfigJson);
public sealed record SaveSkillRequest(string Name, string? Description, string Content);

/// <summary>List/detail view of a library MCP server. The raw config is only
/// returned to its owner — shared entries may contain tokens.</summary>
public sealed record McpServerInfo(
    string Id,
    string Name,
    string Description,
    string Owner,
    bool Mine,
    string? ConfigJson,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SkillInfo(
    string Id,
    string Name,
    string Description,
    string Owner,
    bool Mine,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SkillDetail(
    string Id,
    string Name,
    string Description,
    string Owner,
    bool Mine,
    string Content,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>A skill as delivered to an agent pod (name + SKILL.md content).</summary>
public sealed record SkillPayload(string Name, string Content);

public static class LibraryValidation
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 500;
    public const int MaxMcpConfigBytes = 64_000;
    public const int MaxSkillContentChars = 200_000;

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

    /// <summary>Skill names become directory names under ~/.claude/skills.</summary>
    public static string ValidateSkillName(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is 0 or > MaxNameLength
            || !value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-')
            || value.StartsWith('-') || value.EndsWith('-'))
        {
            throw new ArgumentException("Skill name must be lowercase letters, digits and hyphens (kebab-case).");
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

    /// <summary>The config must be a single JSON object (one server entry).</summary>
    public static string ValidateMcpServerConfig(string? configJson)
    {
        var value = configJson?.Trim() ?? "";
        if (value.Length == 0)
            throw new ArgumentException("MCP server config is required.");
        if (value.Length > MaxMcpConfigBytes)
            throw new ArgumentException("MCP server config is too large.");
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("MCP server config must be a JSON object.");
            if (doc.RootElement.TryGetProperty("mcpServers", out _))
                throw new ArgumentException(
                    "Provide a single server entry, not a full .mcp.json (no top-level mcpServers).");
        }
        catch (JsonException)
        {
            throw new ArgumentException("MCP server config is not valid JSON.");
        }
        return value;
    }

    public static string ValidateSkillContent(string? content)
    {
        var value = content ?? "";
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Skill content is required.");
        if (value.Length > MaxSkillContentChars)
            throw new ArgumentException("Skill content is too large.");
        return value;
    }
}
