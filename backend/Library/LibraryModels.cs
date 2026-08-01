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

public sealed record SaveMcpServerRequest(
    string Name,
    string? Description,
    string Kind,
    string ConfigJson,
    string? SecretJson = null);

/// <summary>List/detail view of a catalog MCP server. ConfigJson is only
/// returned to its owner — shared entries may contain tokens.</summary>
public sealed record McpServerInfo(
    string Id,
    string Name,
    string Description,
    string Owner,
    string Kind,
    bool Mine,
    string? ConfigJson,
    DateTime CreatedAt,
    DateTime UpdatedAt);
