using AgentHub.Api.Licensing;

namespace AgentHub.Api.Library;

/// <summary>
/// Resolves which MCP catalog entries a user can use.
/// Own items are always accessible. Without an enterprise license the org
/// catalog (<c>__org__</c>) is readable by all authenticated users and personal
/// sharing is off. With a license, org and personal shares follow the share matrix.
/// </summary>
public interface ILibraryAccess
{
    Task<IReadOnlyList<McpServerRecord>> ListMcpServersAsync(string owner, CancellationToken ct = default);

    /// <summary>
    /// Resolves saved MCP servers for a session. With <paramref name="strict"/> the
    /// call throws when an id is unknown or inaccessible (create/update validation);
    /// otherwise inaccessible ids are silently dropped (spawn time, e.g. after a
    /// license lapse or a revoked share).
    /// </summary>
    Task<IReadOnlyList<McpServerRecord>> ResolveMcpServersAsync(
        string owner, IReadOnlyCollection<string> ids, bool strict, CancellationToken ct = default);
}

public sealed class LibraryAccessService : ILibraryAccess
{
    private readonly IMcpServerStore _mcpServers;
    private readonly ILibraryShareReader _shares;
    private readonly IEnterpriseLicense _license;

    public LibraryAccessService(
        IMcpServerStore mcpServers,
        ILibraryShareReader shares,
        IEnterpriseLicense license)
    {
        _mcpServers = mcpServers;
        _shares = shares;
        _license = license;
    }

    public async Task<IReadOnlyList<McpServerRecord>> ListMcpServersAsync(
        string owner, CancellationToken ct = default)
    {
        var own = await _mcpServers.ListByOwnerAsync(owner, ct);
        var accessible = new Dictionary<string, McpServerRecord>(StringComparer.Ordinal);
        foreach (var record in own)
            accessible[record.Id] = record;

        if (!_license.Enabled)
        {
            foreach (var org in await _mcpServers.ListByOwnerAsync(McpServerRecord.OrgOwner, ct))
                accessible[org.Id] = org;
        }
        else
        {
            var sharedIds = await SharedIdsAsync(owner, ct);
            if (sharedIds.Count > 0)
            {
                foreach (var shared in await _mcpServers.GetManyAsync(sharedIds, ct))
                {
                    if (shared.Owner != owner)
                        accessible[shared.Id] = shared;
                }
            }
        }

        return accessible.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<McpServerRecord>> ResolveMcpServersAsync(
        string owner, IReadOnlyCollection<string> ids, bool strict, CancellationToken ct = default)
    {
        var distinct = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        if (distinct.Count == 0) return [];

        var records = await _mcpServers.GetManyAsync(distinct, ct);
        var sharedIds = _license.Enabled
            ? await SharedIdsAsync(owner, ct)
            : (IReadOnlyCollection<string>)[];

        var accessible = records
            .Where(r => IsAccessible(r, owner, sharedIds))
            .ToList();

        if (strict && accessible.Count != distinct.Count)
        {
            var found = accessible.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var missing = distinct.Where(i => !found.Contains(i));
            throw new ArgumentException(
                $"Unknown or inaccessible MCP server(s): {string.Join(", ", missing)}.");
        }
        return accessible;
    }

    private bool IsAccessible(
        McpServerRecord record, string owner, IReadOnlyCollection<string> sharedIds)
    {
        if (record.Owner == owner) return true;
        if (!_license.Enabled)
            return record.Owner == McpServerRecord.OrgOwner;
        return sharedIds.Contains(record.Id);
    }

    private Task<IReadOnlyCollection<string>> SharedIdsAsync(string owner, CancellationToken ct)
        => _shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Mcp, owner, ct);
}
