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
    /// Whether the session should expose MCP (secret/mount + <c>SessionInfo.HasMcp</c>).
    /// True when inline config, catalog ids, or ephemeral API sources are present.
    /// </summary>
    public static bool HasMcp(
        string? mcpConfigJson,
        IReadOnlyCollection<string>? mcpServerIds,
        bool hasEphemeralApiSources = false) =>
        !string.IsNullOrWhiteSpace(mcpConfigJson)
        || (mcpServerIds is { Count: > 0 })
        || hasEphemeralApiSources;

    /// <summary>
    /// Strict or lenient resolve followed by <see cref="McpConfigAssembler.Merge"/>.
    /// Returns resolved ids (accessible only) and the effective .mcp.json (or null).
    /// Pass <paramref name="gateway"/> on spawn so <c>kind=api</c> entries get a real URL + token.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Ids, string? EffectiveJson)> ResolveAndAssembleAsync(
        ILibraryAccess library,
        string owner,
        string? inlineMcpConfigJson,
        IReadOnlyCollection<string> mcpServerIds,
        bool strict,
        CancellationToken ct = default,
        McpGatewayAssembleOptions? gateway = null)
    {
        var servers = mcpServerIds.Count == 0
            ? (IReadOnlyList<McpServerRecord>)Array.Empty<McpServerRecord>()
            : await library.ResolveMcpServersAsync(owner, mcpServerIds, strict, ct);
        var ids = servers.Select(s => s.Id).ToList();
        var effective = McpConfigAssembler.Merge(inlineMcpConfigJson, servers, gateway);
        return (ids, effective);
    }
}
