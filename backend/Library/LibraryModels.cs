namespace AgentHub.Api.Library;

/// <summary>
/// A reusable MCP server definition in the personal or org catalog.
/// ConfigJson is a single server entry (raw) or API wrapper config (api).
/// SecretJson holds plaintext secret material in memory after decrypt; the store encrypts at rest.
/// </summary>
public sealed class McpServerRecord
{
    /// <summary>Owner value for admin/org catalog entries.</summary>
    public const string OrgOwner = "__org__";

    public required string Id { get; init; }
    public required string Owner { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary><c>raw</c> or <c>api</c>.</summary>
    public string Kind { get; set; } = "raw";
    public string ConfigJson { get; set; } = "{}";
    /// <summary>Plaintext secrets in memory; null when none. Never return to non-owners.</summary>
    public string? SecretJson { get; set; }
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

/// <param name="SecretJson">
/// On create: null means no secret. On update: null leaves the existing secret unchanged;
/// an empty string clears it; any other value replaces it.
/// </param>
public sealed record SaveMcpServerRequest(
    string Name,
    string? Description,
    string Kind,
    string ConfigJson,
    string? SecretJson = null);

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

/// <summary>List/detail view of a catalog MCP server. ConfigJson is only
/// returned to its owner — shared entries may contain tokens.
/// Secret values are never included; <see cref="HasSecret"/> lets the UI show
/// that a secret is configured.</summary>
public sealed record McpServerInfo(
    string Id,
    string Name,
    string Description,
    string Owner,
    string Kind,
    bool Mine,
    string? ConfigJson,
    bool HasSecret,
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
