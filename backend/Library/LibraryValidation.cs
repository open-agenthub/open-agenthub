using System.Text.Json;
using AgentHub.Api.Library.ApiMcpGateway;

namespace AgentHub.Api.Library;

public static class LibraryValidation
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 500;
    public const int MaxMcpConfigBytes = 64_000;
    public const int MaxSkillContentChars = 200_000;
    public const int MaxCommentLength = 500;
    public const int MaxSkillFiles = 20;
    public const int MaxSkillFilePathLength = 200;
    public const int MaxSkillFilesTotalChars = 500_000;

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

                ApiMcpGatewayHandler.ValidateSafeOutboundUrl(specUrl.GetString()!, "specUrl");
                if (doc.RootElement.TryGetProperty("baseUrl", out var baseUrl)
                    && baseUrl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(baseUrl.GetString()))
                {
                    ApiMcpGatewayHandler.ValidateSafeOutboundUrl(baseUrl.GetString()!, "baseUrl");
                }
            }
        }

        return value;
    }
}
