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
/// in the fallback content column (see SkillStore). Skills are either
/// personal (ProjectId null) or scoped to one of the owner's projects.
/// Every save creates a new immutable version (see SkillVersionRecord).
/// </summary>
public sealed class SkillRecord
{
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>True when the content lives in S3; false = fallback content column.</summary>
    public bool ContentInS3 { get; set; }
    /// <summary>Owner's project this skill belongs to; null = personal library.</summary>
    public string? ProjectId { get; set; }
    /// <summary>Head version number (1-based, monotonically increasing).</summary>
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One immutable revision of a skill's SKILL.md.</summary>
public sealed class SkillVersionRecord
{
    public required string SkillId { get; init; }
    public required int Version { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool ContentInS3 { get; set; }
    /// <summary>Who saved this revision — the owner, or e.g. "session:{id}" for MCP uploads.</summary>
    public string CreatedBy { get; set; } = "";
    /// <summary>Optional change note ("what changed and why").</summary>
    public string Comment { get; set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record SaveMcpServerRequest(string Name, string? Description, string ConfigJson);

/// <summary>An extra file of a skill next to its SKILL.md — scripts, templates,
/// reference documents. Text only; the path is relative to the skill directory.</summary>
public sealed record SkillFile(string Path, string Content);

public sealed record SaveSkillRequest(
    string Name,
    string? Description,
    string Content,
    string? ProjectId = null,
    string? Comment = null,
    string? SavedBy = null,
    // null = keep the current version's files (update) / none (create);
    // [] = remove all files; a list replaces them.
    IReadOnlyList<SkillFile>? Files = null);

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
    string? ProjectId,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SkillDetail(
    string Id,
    string Name,
    string Description,
    string Owner,
    bool Mine,
    string? ProjectId,
    int Version,
    string Content,
    IReadOnlyList<SkillFile> Files,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SkillVersionInfo(
    int Version,
    string Name,
    string Description,
    string CreatedBy,
    string Comment,
    DateTime CreatedAt);

/// <summary>One search result; the score mixes full-text rank and — when an
/// embedding provider is configured — vector similarity.</summary>
public sealed record SkillSearchHit(
    string Id,
    string Name,
    string Description,
    string Owner,
    bool Mine,
    string? ProjectId,
    int Version,
    double Score,
    DateTime UpdatedAt);

/// <summary>A skill as delivered to an agent pod (name + SKILL.md content
/// + extra files such as scripts).</summary>
public sealed record SkillPayload(string Name, string Content, IReadOnlyList<SkillFile> Files);

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

    public const int MaxCommentLength = 500;

    public static string ValidateComment(string? comment)
    {
        var value = comment?.Trim() ?? "";
        if (value.Length > MaxCommentLength)
            throw new ArgumentException($"Comment must be at most {MaxCommentLength} characters.");
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

    public const int MaxSkillFiles = 20;
    public const int MaxSkillFilePathLength = 200;
    public const int MaxSkillFilesTotalChars = 500_000;

    /// <summary>Validates the extra files of a skill. Paths become real paths under
    /// ~/.claude/skills/{name}/ in agent pods, so they must be strictly relative,
    /// shallow and made of safe segments. Returns the normalized list.</summary>
    public static IReadOnlyList<SkillFile>? ValidateSkillFiles(IReadOnlyList<SkillFile>? files)
    {
        if (files is null) return null;
        if (files.Count > MaxSkillFiles)
            throw new ArgumentException($"A skill may have at most {MaxSkillFiles} extra files.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var result = new List<SkillFile>(files.Count);
        foreach (var file in files)
        {
            var path = ValidateSkillFilePath(file.Path);
            if (!seen.Add(path))
                throw new ArgumentException($"Duplicate file path: {path}");
            var content = file.Content ?? "";
            if (content.Contains('\0'))
                throw new ArgumentException($"File {path} is not text.");
            if (content.Length > MaxSkillContentChars)
                throw new ArgumentException($"File {path} is too large.");
            total += content.Length;
            if (total > MaxSkillFilesTotalChars)
                throw new ArgumentException("The skill's files are too large in total.");
            result.Add(new SkillFile(path, content));
        }
        return result;
    }

    public static string ValidateSkillFilePath(string? path)
    {
        var value = (path ?? "").Trim().Replace('\\', '/');
        if (value.Length is 0 or > MaxSkillFilePathLength)
            throw new ArgumentException("File paths must be 1-200 characters.");
        var segments = value.Split('/');
        if (segments.Length > 3)
            throw new ArgumentException($"File path is nested too deeply: {value}");
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".."
                || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                || segment.StartsWith('.'))
            {
                throw new ArgumentException($"Invalid file path segment in: {value}");
            }
        }
        if (string.Equals(value, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SKILL.md is the skill content itself, not an extra file.");
        return value;
    }
}
