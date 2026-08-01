using AgentHub.Api.Library;

namespace AgentHub.Api.Services;

/// <summary>
/// Resolves catalog MCP servers and merges them with inline session config.
/// Create/update use <paramref name="strict"/> = true so bad shape/config fails
/// before the session row is written; spawn uses false so revoked shares drop out.
/// </summary>
public static class SessionMcpConfig
{
    /// <summary>
    /// Strict or lenient resolve followed by <see cref="McpConfigAssembler.Merge"/>.
    /// Returns resolved ids (accessible only) and the effective .mcp.json (or null).
    /// </summary>
    public static async Task<(IReadOnlyList<string> Ids, string? EffectiveJson)> ResolveAndAssembleAsync(
        ILibraryAccess library,
        string owner,
        string? inlineMcpConfigJson,
        IReadOnlyCollection<string> mcpServerIds,
        bool strict,
        CancellationToken ct = default)
    {
        var servers = mcpServerIds.Count == 0
            ? (IReadOnlyList<McpServerRecord>)Array.Empty<McpServerRecord>()
            : await library.ResolveMcpServersAsync(owner, mcpServerIds, strict, ct);
        var ids = servers.Select(s => s.Id).ToList();
        var effective = McpConfigAssembler.Merge(inlineMcpConfigJson, servers);
        return (ids, effective);
    }
}
